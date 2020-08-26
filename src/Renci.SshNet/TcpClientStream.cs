using System;
using System.Collections.Generic;
using System.IO;
using Renci.SshNet.Channels;
using Renci.SshNet.Common;
using System.Threading;
using Renci.SshNet.Messages.Connection;

namespace Renci.SshNet {
	/// <summary>
	/// Provides a Stream implementation operating on a remote TCP client channel.
	/// </summary>
	public class TcpClientStream : Stream {
        private readonly ISession _session;
        private readonly int _bufferSize;
        private readonly Queue<byte> _incoming;
        private readonly Queue<byte> _outgoing;
        private IChannelSession _channel;
        private bool _isDisposed;

        /// <summary>
        /// Occurs when data was received.
        /// </summary>
        public event EventHandler<ShellDataEventArgs> DataReceived;

        /// <summary>
        /// Occurs when an error occurred.
        /// </summary>
        public event EventHandler<ExceptionEventArgs> ErrorOccurred;

        /// <summary>
        /// Gets a value that indicates whether data is available on the <see cref="ShellStream"/> to be read.
        /// </summary>
        /// <value>
        /// <c>true</c> if data is available to be read; otherwise, <c>false</c>.
        /// </value>
        public bool DataAvailable {
            get {
                lock (_incoming) {
                    return _incoming.Count > 0;
                }
            }
        }

        /// <summary>
        /// Gets the number of bytes that will be written to the internal buffer.
        /// </summary>
        /// <value>
        /// The number of bytes that will be written to the internal buffer.
        /// </value>
        internal int BufferSize {
            get { return _bufferSize; }
        }

        internal TcpClientStream(ISession session, string remoteHost, uint port) {
            _session = session;
            _bufferSize = 64 * 1024;
            _incoming = new Queue<byte>();
            _outgoing = new Queue<byte>();

            _channel = _session.CreateChannelSession();
            _channel.DataReceived += Channel_DataReceived;
            _channel.Closed += Channel_Closed;
            _session.Disconnected += Session_Disconnected;
            _session.ErrorOccured += Session_ErrorOccured;

            try {
                _channel.Open(new DirectTcpipChannelInfo(remoteHost, port, "0.0.0.0", 0));
            } catch {
                UnsubscribeFromSessionEvents(session);
                _channel.Dispose();
                throw;
            }
        }

        #region Stream overide methods

        /// <summary>
        /// Gets a value indicating whether the current stream supports reading.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the stream supports reading; otherwise, <c>false</c>.
        /// </returns>
        public override bool CanRead {
            get { return true; }
        }

        /// <summary>
        /// Gets a value indicating whether the current stream supports seeking.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the stream supports seeking; otherwise, <c>false</c>.
        /// </returns>
        public override bool CanSeek {
            get { return false; }
        }

        /// <summary>
        /// Gets a value indicating whether the current stream supports writing.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the stream supports writing; otherwise, <c>false</c>.
        /// </returns>
        public override bool CanWrite {
            get { return true; }
        }

        /// <summary>
        /// Clears all buffers for this stream and causes any buffered data to be written to the underlying device.
        /// </summary>
        /// <exception cref="T:System.IO.IOException">An I/O error occurs.</exception>
        /// <exception cref="ObjectDisposedException">Methods were called after the stream was closed.</exception>
        public override void Flush() {
            if (_channel == null) {
                throw new ObjectDisposedException("ShellStream");
            }

            if (_outgoing.Count > 0) {
                _channel.SendData(_outgoing.ToArray());
                _outgoing.Clear();
            }
        }

        /// <summary>
        /// Gets the length in bytes of the stream.
        /// </summary>
        /// <returns>A long value representing the length of the stream in bytes.</returns>
        /// <exception cref="NotSupportedException">A class derived from Stream does not support seeking.</exception>
        /// <exception cref="ObjectDisposedException">Methods were called after the stream was closed.</exception>
        public override long Length {
            get {
                lock (_incoming) {
                    return _incoming.Count;
                }
            }
        }

        /// <summary>
        /// Gets or sets the position within the current stream.
        /// </summary>
        /// <returns>
        /// The current position within the stream.
        /// </returns>
        /// <exception cref="T:System.IO.IOException">An I/O error occurs.</exception>
        /// <exception cref="T:System.NotSupportedException">The stream does not support seeking.</exception>
        /// <exception cref="T:System.ObjectDisposedException">Methods were called after the stream was closed.</exception>
        public override long Position {
            get { return 0; }
            set { throw new NotSupportedException(); }
        }

        /// <summary>
        /// Reads a sequence of bytes from the current stream and advances the position within the stream by the number of bytes read.
        /// </summary>
        /// <param name="buffer">An array of bytes. When this method returns, the buffer contains the specified byte array with the values between <paramref name="offset"/> and (<paramref name="offset"/> + <paramref name="count"/> - 1) replaced by the bytes read from the current source.</param>
        /// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> at which to begin storing the data read from the current stream.</param>
        /// <param name="count">The maximum number of bytes to be read from the current stream.</param>
        /// <returns>
        /// The total number of bytes read into the buffer. This can be less than the number of bytes requested if that many bytes are not currently available, or zero (0) if the end of the stream has been reached.
        /// </returns>
        /// <exception cref="T:System.ArgumentException">The sum of <paramref name="offset"/> and <paramref name="count"/> is larger than the buffer length. </exception>
        /// <exception cref="T:System.ArgumentNullException"><paramref name="buffer"/> is <c>null</c>.</exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="count"/> is negative.</exception>
        /// <exception cref="T:System.IO.IOException">An I/O error occurs.</exception>   
        /// <exception cref="T:System.NotSupportedException">The stream does not support reading.</exception>   
        /// <exception cref="T:System.ObjectDisposedException">Methods were called after the stream was closed.</exception>
        public override int Read(byte[] buffer, int offset, int count) {
            var i = 0;

            lock (_incoming) {
                if (count > 0 && _incoming.Count == 0) Monitor.Wait(_incoming);
                for (; i < count && _incoming.Count > 0; i++) {
                    buffer[offset + i] = _incoming.Dequeue();
                }
            }

            return i;
        }

        /// <summary>
        /// This method is not supported.
        /// </summary>
        /// <param name="offset">A byte offset relative to the <paramref name="origin"/> parameter.</param>
        /// <param name="origin">A value of type <see cref="T:System.IO.SeekOrigin"/> indicating the reference point used to obtain the new position.</param>
        /// <returns>
        /// The new position within the current stream.
        /// </returns>
        /// <exception cref="T:System.IO.IOException">An I/O error occurs.</exception>
        /// <exception cref="T:System.NotSupportedException">The stream does not support seeking, such as if the stream is constructed from a pipe or console output.</exception>
        /// <exception cref="T:System.ObjectDisposedException">Methods were called after the stream was closed.</exception>
        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        /// <summary>
        /// This method is not supported.
        /// </summary>
        /// <param name="value">The desired length of the current stream in bytes.</param>
        /// <exception cref="T:System.IO.IOException">An I/O error occurs.</exception>
        /// <exception cref="T:System.NotSupportedException">The stream does not support both writing and seeking, such as if the stream is constructed from a pipe or console output.</exception>
        /// <exception cref="T:System.ObjectDisposedException">Methods were called after the stream was closed.</exception>
        public override void SetLength(long value) {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Writes a sequence of bytes to the current stream and advances the current position within this stream by the number of bytes written.
        /// </summary>
        /// <param name="buffer">An array of bytes. This method copies <paramref name="count"/> bytes from <paramref name="buffer"/> to the current stream.</param>
        /// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> at which to begin copying bytes to the current stream.</param>
        /// <param name="count">The number of bytes to be written to the current stream.</param>
        /// <exception cref="T:System.ArgumentException">The sum of <paramref name="offset"/> and <paramref name="count"/> is greater than the buffer length.</exception>
        /// <exception cref="T:System.ArgumentNullException"><paramref name="buffer"/> is <c>null</c>.</exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="count"/> is negative.</exception>
        /// <exception cref="T:System.IO.IOException">An I/O error occurs.</exception>
        /// <exception cref="T:System.NotSupportedException">The stream does not support writing.</exception>
        /// <exception cref="T:System.ObjectDisposedException">Methods were called after the stream was closed.</exception>
        public override void Write(byte[] buffer, int offset, int count) {
            foreach (var b in buffer.Take(offset, count)) {
                if (_outgoing.Count == _bufferSize) {
                    Flush();
                }

                _outgoing.Enqueue(b);
            }
            Flush();
        }

        #endregion

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="Stream"/> and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing) {
            base.Dispose(disposing);

            if (_isDisposed)
                return;

            if (disposing) {
                UnsubscribeFromSessionEvents(_session);

                if (_channel != null) {
                    _channel.DataReceived -= Channel_DataReceived;
                    _channel.Closed -= Channel_Closed;
                    _channel.Dispose();
                    _channel = null;
                }

                _isDisposed = true;

                lock (_incoming) Monitor.PulseAll(_incoming);
            } else {
                UnsubscribeFromSessionEvents(_session);
            }
        }

        /// <summary>
        /// Unsubscribes the current <see cref="ShellStream"/> from session events.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <remarks>
        /// Does nothing when <paramref name="session"/> is <c>null</c>.
        /// </remarks>
        private void UnsubscribeFromSessionEvents(ISession session) {
            if (session == null)
                return;

            session.Disconnected -= Session_Disconnected;
            session.ErrorOccured -= Session_ErrorOccured;
        }

        private void Session_ErrorOccured(object sender, ExceptionEventArgs e) {
            OnRaiseError(e);
        }

        private void Session_Disconnected(object sender, EventArgs e) {
            if (_channel != null)
                _channel.Dispose();
        }

        private void Channel_Closed(object sender, ChannelEventArgs e) {
            //  TODO:   Do we need to call dispose here ??
            Dispose();
        }

        private void Channel_DataReceived(object sender, ChannelDataEventArgs e) {
            lock (_incoming) {
                foreach (var b in e.Data)
                    _incoming.Enqueue(b);

                Monitor.PulseAll(_incoming);
            }

            OnDataReceived(e.Data);
        }

        private void OnRaiseError(ExceptionEventArgs e) {
            var handler = ErrorOccurred;
            if (handler != null) {
                handler(this, e);
            }
        }

        private void OnDataReceived(byte[] data) {
            var handler = DataReceived;
            if (handler != null) {
                handler(this, new ShellDataEventArgs(data));
            }
        }
    }
}
