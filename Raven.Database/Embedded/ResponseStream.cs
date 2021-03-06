﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Raven.Abstractions.Logging;
using Raven.Abstractions.Util;

namespace Raven.Database.Embedded
{
	// This steam accepts writes from the server/app, buffers them internally, and returns the data via Reads
	// when requested by the client.
	internal class ResponseStream : Stream
	{
		private readonly ILog log = LogManager.GetCurrentClassLogger();

		private volatile bool _disposed;
		private bool _aborted;
		private Exception _abortException;
		private ConcurrentQueue<byte[]> _bufferedData;
		private byte[] _topBuffer;
		private int _topBufferOffset;
		private int _topBufferCount;
		private SemaphoreSlim _readLock;
		private SemaphoreSlim _writeLock;
		private TaskCompletionSource<object> _readWaitingForData;
		private object _signalReadLock;

		private Action _onFirstWrite;

		private readonly bool _enableLogging;

		private bool _firstWrite;

		private bool _wroteZero;

		private Guid _id;

		internal ResponseStream(Action onFirstWrite, bool enableLogging)
		{
			if (onFirstWrite == null)
			{
				throw new ArgumentNullException("onFirstWrite");
			}
			_onFirstWrite = onFirstWrite;
			_enableLogging = enableLogging;
			_firstWrite = true;
			_id = Guid.NewGuid();

			_readLock = new SemaphoreSlim(1, 1);
			_writeLock = new SemaphoreSlim(1, 1);
			_bufferedData = new ConcurrentQueue<byte[]>();
			_readWaitingForData = new TaskCompletionSource<object>();
			_signalReadLock = new object();
		}

		public override bool CanRead
		{
			get { return true; }
		}

		public override bool CanSeek
		{
			get { return false; }
		}

		public override bool CanWrite
		{
			get { return true; }
		}

		#region NotSupported

		public override long Length
		{
			get { throw new NotSupportedException(); }
		}

		public override long Position
		{
			get { throw new NotSupportedException(); }
			set { throw new NotSupportedException(); }
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		#endregion NotSupported

		public override void Flush()
		{
			CheckDisposed();

			_writeLock.Wait();
			try
			{
				FirstWrite();
			}
			finally
			{
				_writeLock.Release();
			}

			// TODO: Wait for data to drain?
		}

		public override Task FlushAsync(CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				var tcs = new TaskCompletionSource<object>();
				tcs.TrySetCanceled();
				return tcs.Task;
			}

			Flush();

			// TODO: Wait for data to drain?

			return new CompletedTask();
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			VerifyBuffer(buffer, offset, count, allowEmpty: false);
			_readLock.Wait();
			try
			{
				int totalRead = 0;
				do
				{
					// Don't drain buffered data when signaling an abort.
					CheckAborted();
					if (_topBufferCount <= 0)
					{
						byte[] topBuffer;
						while (!_bufferedData.TryDequeue(out topBuffer))
						{
							if (_disposed)
							{
								CheckAborted();
								// Graceful close
								return totalRead;
							}
							WaitForDataAsync().Wait();
						}
						_topBuffer = topBuffer;
						_topBufferOffset = 0;
						_topBufferCount = topBuffer.Length;
					}
					int actualCount = Math.Min(count, _topBufferCount);
					Buffer.BlockCopy(_topBuffer, _topBufferOffset, buffer, offset, actualCount);
					_topBufferOffset += actualCount;
					_topBufferCount -= actualCount;
					totalRead += actualCount;
					offset += actualCount;
					count -= actualCount;
				}
				while (count > 0 && (_topBufferCount > 0 || _bufferedData.Count > 0));
				// Keep reading while there is more data available and we have more space to put it in.
				return totalRead;
			}
			finally
			{
				_readLock.Release();
			}
		}

		public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			VerifyBuffer(buffer, offset, count, allowEmpty: false);
			CancellationTokenRegistration registration = cancellationToken.Register(Abort);
			await _readLock.WaitAsync(cancellationToken);
			try
			{
				int totalRead = 0;
				do
				{
					// Don't drained buffered data on abort.
					CheckAborted();
					if (_topBufferCount <= 0)
					{
						byte[] topBuffer;
						while (!_bufferedData.TryDequeue(out topBuffer))
						{
							if (_disposed)
							{
								CheckAborted();
								// Graceful close
								return totalRead;
							}
							await WaitForDataAsync();
						}
						_topBuffer = topBuffer;
						_topBufferOffset = 0;
						_topBufferCount = topBuffer.Length;
					}
					int actualCount = Math.Min(count, _topBufferCount);
					Buffer.BlockCopy(_topBuffer, _topBufferOffset, buffer, offset, actualCount);
					_topBufferOffset += actualCount;
					_topBufferCount -= actualCount;
					totalRead += actualCount;
					offset += actualCount;
					count -= actualCount;
				}
				while (count > 0 && (_topBufferCount > 0 || _bufferedData.Count > 0));
				// Keep reading while there is more data available and we have more space to put it in.
				return totalRead;
			}
			finally
			{
				registration.Dispose();
				_readLock.Release();
			}
		}

		// Called under write-lock.
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void FirstWrite()
		{
			if (_firstWrite)
			{
				_firstWrite = false;
				_onFirstWrite();
			}
		}

		// Write with count 0 will still trigger OnFirstWrite
		public override void Write(byte[] buffer, int offset, int count)
		{
			VerifyBuffer(buffer, offset, count, allowEmpty: true);

			_writeLock.Wait();
			try
			{
				CheckDisposed();
				if (count == 0)
				{
					_wroteZero = true;
					FirstWrite();
					return;
				}

				Debug.Assert(_wroteZero == false);

				// Copies are necessary because we don't know what the caller is going to do with the buffer afterwards.
				var internalBuffer = new byte[count];
				Buffer.BlockCopy(buffer, offset, internalBuffer, 0, count);

				if (_enableLogging)
					log.Info("ResponseStream ({0}). Write. Content: {1}", _id, Encoding.UTF8.GetString(internalBuffer));

				_bufferedData.Enqueue(internalBuffer);

				SignalDataAvailable();
				FirstWrite();
			}
			finally
			{
				_writeLock.Release();
			}
		}

		public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
		{
			Write(buffer, offset, count);
			var tcs = new TaskCompletionSource<object>(state);
			tcs.TrySetResult(null);
			IAsyncResult result = tcs.Task;
			if (callback != null)
			{
				callback(result);
			}
			return result;
		}

		public override void EndWrite(IAsyncResult asyncResult)
		{
		}

		public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			VerifyBuffer(buffer, offset, count, allowEmpty: true);
			if (cancellationToken.IsCancellationRequested)
			{
				var tcs = new TaskCompletionSource<object>();
				tcs.TrySetCanceled();
				return tcs.Task;
			}

			Write(buffer, offset, count);
			return new CompletedTask();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void VerifyBuffer(byte[] buffer, int offset, int count, bool allowEmpty)
		{
			if (buffer == null)
			{
				throw new ArgumentNullException("buffer");
			}
			if (offset < 0 || offset > buffer.Length)
			{
				throw new ArgumentOutOfRangeException("offset", offset, string.Empty);
			}
			if (count < 0 || count > buffer.Length - offset
				|| (!allowEmpty && count == 0))
			{
				throw new ArgumentOutOfRangeException("count", count, string.Empty);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void SignalDataAvailable()
		{
			// Dispatch, as TrySetResult will synchronously execute the waiters callback and block our Write.
			Task.Factory.StartNew(() => _readWaitingForData.TrySetResult(null));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private Task WaitForDataAsync()
		{
			// Prevent race with Dispose
			lock (_signalReadLock)
			{
				_readWaitingForData = new TaskCompletionSource<object>();

				if (!_bufferedData.IsEmpty || _disposed)
				{
					// Race, data could have arrived before we created the TCS.
					_readWaitingForData.TrySetResult(null);
				}

				return _readWaitingForData.Task;
			}
		}

		internal void Abort()
		{
			Abort(new OperationCanceledException());
		}

		internal void Abort(Exception innerException)
		{
			Contract.Requires(innerException != null);
			_aborted = true;
			_abortException = innerException;
			Dispose();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void CheckAborted()
		{
			if (_aborted)
			{
				throw new IOException(string.Empty, _abortException);
			}
		}

		[SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "_writeLock", Justification = "ODEs from the locks would mask IOEs from abort.")]
		[SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "_readLock", Justification = "Data can still be read unless we get aborted.")]
		protected override void Dispose(bool disposing)
		{
			if (_disposed)
				return;

			if (disposing)
			{
				_writeLock.Wait();
				try
				{
					// Prevent race with WaitForDataAsync
					lock (_signalReadLock)
					{
						// Throw for further writes, but not reads.  Allow reads to drain the buffered data and then return 0 for further reads.
						_disposed = true;
						_readWaitingForData.TrySetResult(null);
					}
				}
				finally
				{
					_writeLock.Release();
				}
			}

			base.Dispose(disposing);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void CheckDisposed()
		{
			if (_disposed)
			{
				throw new ObjectDisposedException(GetType().FullName);
			}
		}
	}
}
