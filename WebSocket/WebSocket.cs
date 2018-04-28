﻿#region Related components
using System;
using System.Text;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Components.WebSockets.Implementation
{
	/// <summary>
	/// Implementation of the <see cref="System.Net.WebSockets.WebSocket">System.Net.WebSockets.WebSocket</see> abstract class with more useful properties and methods
	/// </summary>
	public class WebSocket : System.Net.WebSockets.WebSocket, IDisposable
	{

		#region Properties
		const int MAX_PING_PONG_PAYLOAD_LENGTH = 125;

		readonly Func<MemoryStream> _recycledStreamFactory;
		readonly Stream _stream;
		readonly bool _includeExceptionInCloseResponse;
		readonly IPingPongManager _pingPongManager;
		readonly bool _usePerMessageDeflate = false;
		WebSocketState _state;
		WebSocketMessageType _continuationFrameMessageType = WebSocketMessageType.Binary;
		WebSocketCloseStatus? _closeStatus;
		string _closeStatusDescription;
		bool _isContinuationFrame;
		bool _tryGetBufferFailureLogged = false;
		bool _writting = false, _disposed = false;
		CancellationTokenSource _readingCTS;
		ConcurrentQueue<ArraySegment<byte>> _buffers = new ConcurrentQueue<ArraySegment<byte>>();

		public event EventHandler<PongEventArgs> Pong;

		/// <summary>
		/// Gets the state that indicates the reason why the remote endpoint initiated the close handshake
		/// </summary>
		public override WebSocketCloseStatus? CloseStatus => this._closeStatus;

		/// <summary>
		/// Gets the description to describe the reason why the connection was closed
		/// </summary>
		public override string CloseStatusDescription => this._closeStatusDescription;

		/// <summary>
		/// Gets the current state of the WebSocket connection
		/// </summary>
		public override WebSocketState State { get { return this._state; } }

		/// <summary>
		/// Gets the subprotocol that was negotiated during the opening handshake
		/// </summary>
		public override string SubProtocol => null;

		/// <summary>
		/// Gets or sets the keep-alive interval (seconds)
		/// </summary>
		public TimeSpan KeepAliveInterval { get; set; }

		/// <summary>
		/// Gets the identity of the WebSocket connection
		/// </summary>
		public Guid ID { get; }

		/// <summary>
		/// Gets the time when the WebSocket connection is established
		/// </summary>
		public DateTime Time { get; } = DateTime.Now;

		/// <summary>
		/// Gets the path from the requesting uri of the WebSocket connection
		/// </summary>
		public string UriPath { get; internal set; }

		/// <summary>
		/// Gets the local endpoint of the WebSocket connection
		/// </summary>
		public EndPoint LocalEndPoint { get; internal set; }

		/// <summary>
		/// Gets the remote endpoint of the WebSocket connection
		/// </summary>
		public EndPoint RemoteEndPoint { get; internal set; }

		/// <summary>
		/// Gets the state that indicates the WebSocket connection is client mode or not (client mode means the WebSocket connection is connected to a remote endpoint)
		/// </summary>
		public bool IsClient { get; private set; }
		#endregion

		/// <summary>
		/// Creates new an instance of WebSocket
		/// </summary>
		/// <param name="id"></param>
		/// <param name="isClient"></param>
		/// <param name="recycledStreamFactory"></param>
		/// <param name="stream"></param>
		/// <param name="keepAliveInterval"></param>
		/// <param name="secWebSocketExtensions"></param>
		/// <param name="includeExceptionInCloseResponse"></param>
		internal WebSocket(Guid id, bool isClient, Func<MemoryStream> recycledStreamFactory, Stream stream, TimeSpan keepAliveInterval, string secWebSocketExtensions, bool includeExceptionInCloseResponse)
		{
			this.ID = id;
			this.IsClient = isClient;

			this._recycledStreamFactory = recycledStreamFactory ?? WebSocketHelper.GetRecyclableMemoryStreamFactory();
			this._stream = stream;
			this._state = WebSocketState.Open;
			this._readingCTS = new CancellationTokenSource();
			this._includeExceptionInCloseResponse = includeExceptionInCloseResponse;

			this.KeepAliveInterval = keepAliveInterval;
			if (this.KeepAliveInterval.Ticks < 0)
				throw new ArgumentException("Keep-Alive interval must be Zero or positive", nameof(keepAliveInterval));

			if (this.KeepAliveInterval == TimeSpan.Zero)
				Events.Log.KeepAliveIntervalZero(this.ID);
			else
				this._pingPongManager = new PingPongManager(this.ID, this, this.KeepAliveInterval, this._readingCTS.Token);

			if (secWebSocketExtensions?.IndexOf("permessage-deflate") >= 0)
			{
				this._usePerMessageDeflate = true;
				Events.Log.UsePerMessageDeflate(this.ID);
			}
			else
				Events.Log.NoMessageCompression(this.ID);
		}

		#region Receive messages
		/// <summary>
		/// Receives data from the WebSocket connection
		/// </summary>
		/// <param name="buffer">The buffer to copy data into</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>The web socket result details</returns>
		public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
		{
			try
			{
				// we may receive control frames so reading needs to happen in an infinite loop
				while (true)
				{
					// allow this operation to be cancelled from iniside OR outside this instance
					using (var cts = CancellationTokenSource.CreateLinkedTokenSource(this._readingCTS.Token, cancellationToken))
					{
						WebSocketFrame frame = null;
						try
						{
							frame = await FrameReaderWriter.ReadAsync(this._stream, buffer, cts.Token).ConfigureAwait(false);
							Events.Log.ReceivedFrame(this.ID, frame.OpCode, frame.IsFinBitSet, frame.Count);
						}
						catch (InternalBufferOverflowException ex)
						{
							await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.MessageTooBig, "Frame is too large to fit in buffer. Use message fragmentation.", ex).ConfigureAwait(false);
							throw;
						}
						catch (ArgumentOutOfRangeException ex)
						{
							await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.ProtocolError, "Payload length is out of range", ex).ConfigureAwait(false);
							throw;
						}
						catch (EndOfStreamException ex)
						{
							await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.InvalidPayloadData, "Unexpected end of stream encountered", ex).ConfigureAwait(false);
							throw;
						}
						catch (OperationCanceledException ex)
						{
							await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.EndpointUnavailable, "Operation cancelled", ex).ConfigureAwait(false);
							throw;
						}
						catch (Exception ex)
						{
							await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.InternalServerError, "Error reading WebSocket frame", ex).ConfigureAwait(false);
							throw;
						}

						switch (frame.OpCode)
						{
							case WebSocketOpCode.ConnectionClose:
								return await this.RespondToCloseFrameAsync(frame, buffer, cts.Token).ConfigureAwait(false);

							case WebSocketOpCode.Ping:
								await this.SendPongAsync(new ArraySegment<byte>(buffer.Array, buffer.Offset, frame.Count), cts.Token).ConfigureAwait(false);
								break;

							case WebSocketOpCode.Pong:
								this.Pong?.Invoke(this, new PongEventArgs(new ArraySegment<byte>(buffer.Array, frame.Count, buffer.Offset)));
								break;

							case WebSocketOpCode.TextFrame:
								if (!frame.IsFinBitSet)
									this._continuationFrameMessageType = WebSocketMessageType.Text; // continuation frames will follow, record the message type Text
								return new WebSocketReceiveResult(frame.Count, WebSocketMessageType.Text, frame.IsFinBitSet);

							case WebSocketOpCode.BinaryFrame:
								if (!frame.IsFinBitSet)
									this._continuationFrameMessageType = WebSocketMessageType.Binary; // continuation frames will follow, record the message type Binary
								return new WebSocketReceiveResult(frame.Count, WebSocketMessageType.Binary, frame.IsFinBitSet);

							case WebSocketOpCode.ContinuationFrame:
								return new WebSocketReceiveResult(frame.Count, this._continuationFrameMessageType, frame.IsFinBitSet);

							default:
								var ex = new NotSupportedException($"Unknown WebSocket opcode {frame.OpCode}");
								await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.ProtocolError, ex.Message, ex).ConfigureAwait(false);
								throw ex;
						}
					}
				}
			}
			catch (Exception ex)
			{
				// most exceptions will be caught closer to their source to send an appropriate close message (and set the WebSocketState)
				// however, if an unhandled exception is encountered and a close message not sent then send one here
				if (this._state == WebSocketState.Open)
					await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.InternalServerError, "Got an unexpected error while reading from WebSocket", ex).ConfigureAwait(false);
				throw;
			}
		}
		#endregion

		#region Send messages
		/// <summary>
		/// Sends data over the WebSocket connection 
		/// </summary>
		/// <param name="buffer">The buffer containing data to send</param>
		/// <param name="messageType">The message type. Can be Text or Binary</param>
		/// <param name="endOfMessage">True if this message is a standalone message (this is the norm)
		/// If it is a multi-part message then false (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
		{
			using (var stream = this._recycledStreamFactory())
			{
				var opCode = this.GetOpCode(messageType);

				// NOTE: Compression is currently work in progress and should NOT be used.
				// The code below is very inefficient for small messages. Ideally we would like to have some sort of moving window of data to get the best compression.
				// And we don't want to create new buffers which is bad for GC.
				if (this._usePerMessageDeflate)
				{
					var compressedBuffer = buffer.Compress();
					FrameReaderWriter.Write(opCode, compressedBuffer, stream, endOfMessage, this.IsClient);
					Events.Log.SendingFrame(this.ID, opCode, endOfMessage, compressedBuffer.Count, true);
				}

				else
				{
					FrameReaderWriter.Write(opCode, buffer, stream, endOfMessage, this.IsClient);
					Events.Log.SendingFrame(this.ID, opCode, endOfMessage, buffer.Count, false);
				}

				await this.WriteStreamToNetworkAsync(stream, cancellationToken).ConfigureAwait(false);
				this._isContinuationFrame = !endOfMessage;
			}
		}

		/// <summary>
		/// Sends data over the WebSocket connection 
		/// </summary>
		/// <param name="message">The buffer containing data to send</param>
		/// <param name="endOfMessage">True if this message is a standalone message (this is the norm)
		/// If it is a multi-part message then false (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public Task SendAsync(string message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.SendAsync(message.ToArraySegment(), WebSocketMessageType.Text, endOfMessage, cancellationToken);
		}

		/// <summary>
		/// Sends data over the WebSocket connection 
		/// </summary>
		/// <param name="message">The buffer containing data to send</param>
		/// <param name="endOfMessage">True if this message is a standalone message (this is the norm)
		/// If it is a multi-part message then false (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public Task SendAsync(byte[] message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.SendAsync(message.ToArraySegment(), WebSocketMessageType.Binary, endOfMessage, cancellationToken);
		}
		#endregion

		#region Send ping/pong
		/// <summary>
		/// Calls this automatically from server side each KeepAliveInterval period (ping payload must be 125 bytes or less)
		/// </summary>
		public async Task SendPingAsync(ArraySegment<byte> payload, CancellationToken cancellationToken)
		{
			if (payload.Count > MAX_PING_PONG_PAYLOAD_LENGTH)
				throw new InvalidOperationException($"Cannot send Ping: Max ping message size {MAX_PING_PONG_PAYLOAD_LENGTH} exceeded: {payload.Count}");

			if (this._state == WebSocketState.Open)
				using (var stream = this._recycledStreamFactory())
				{
					FrameReaderWriter.Write(WebSocketOpCode.Ping, payload, stream, true, this.IsClient);
					Events.Log.SendingFrame(this.ID, WebSocketOpCode.Ping, true, payload.Count, false);
					await this.WriteStreamToNetworkAsync(stream, cancellationToken).ConfigureAwait(false);
				}
		}

		/// <summary>
		/// Calls this when got ping messages (pong payload must be 125 bytes or less, pong should contain the same payload as the ping)
		/// </summary>
		/// <param name="payload"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		async Task SendPongAsync(ArraySegment<byte> payload, CancellationToken cancellationToken)
		{
			// exceeded max length
			if (payload.Count > MAX_PING_PONG_PAYLOAD_LENGTH)
			{
				var ex = new InvalidOperationException($"Max ping message size {MAX_PING_PONG_PAYLOAD_LENGTH} exceeded: {payload.Count}");
				await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.ProtocolError, ex.Message, ex).ConfigureAwait(false);
				throw ex;
			}

			try
			{
				if (this._state == WebSocketState.Open)
					using (var stream = this._recycledStreamFactory())
					{
						FrameReaderWriter.Write(WebSocketOpCode.Pong, payload, stream, true, this.IsClient);
						Events.Log.SendingFrame(this.ID, WebSocketOpCode.Pong, true, payload.Count, false);
						await this.WriteStreamToNetworkAsync(stream, cancellationToken).ConfigureAwait(false);
					}
			}
			catch (Exception ex)
			{
				await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.EndpointUnavailable, "Unable to send Pong response", ex).ConfigureAwait(false);
				throw;
			}
		}

		/// <summary>
		/// Called when a Pong frame is received
		/// </summary>
		/// <param name="args"></param>
		protected virtual void OnPong(PongEventArgs args)
		{
			this.Pong?.Invoke(this, args);
		}
		#endregion

		#region Close connection
		/// <summary>
		/// Polite close (use the close handshake)
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="cancellationToken">The timeout cancellation token</param>
		/// <returns></returns>
		public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, CancellationToken cancellationToken)
		{
			if (this._state == WebSocketState.Open)
			{
				using (var stream = this._recycledStreamFactory())
				{
					var buffer = this.BuildClosePayload(closeStatus, closeStatusDescription);
					FrameReaderWriter.Write(WebSocketOpCode.ConnectionClose, buffer, stream, true, this.IsClient);
					Events.Log.CloseHandshakeStarted(this.ID, closeStatus, closeStatusDescription);
					Events.Log.SendingFrame(this.ID, WebSocketOpCode.ConnectionClose, true, buffer.Count, false);
					await this.WriteStreamToNetworkAsync(stream, cancellationToken).ConfigureAwait(false);
					this._state = WebSocketState.CloseSent;
				}
			}
			else
				Events.Log.InvalidStateBeforeClose(this.ID, this._state);
		}

		/// <summary>
		/// Fire and forget close
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="cancellationToken">The timeout cancellation token</param>
		/// <returns></returns>
		public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, CancellationToken cancellationToken)
		{
			if (this._state == WebSocketState.Open)
			{
				// set this before we write to the network because the write may fail
				this._state = WebSocketState.Closed;

				using (var stream = this._recycledStreamFactory())
				{
					var buffer = this.BuildClosePayload(closeStatus, closeStatusDescription);
					FrameReaderWriter.Write(WebSocketOpCode.ConnectionClose, buffer, stream, true, this.IsClient);
					Events.Log.CloseOutputNoHandshake(this.ID, closeStatus, closeStatusDescription);
					Events.Log.SendingFrame(this.ID, WebSocketOpCode.ConnectionClose, true, buffer.Count, false);
					await this.WriteStreamToNetworkAsync(stream, cancellationToken).ConfigureAwait(false);
				}
			}
			else
				Events.Log.InvalidStateBeforeCloseOutput(this.ID, this._state);

			// cancel pending reads
			this._readingCTS.Cancel();
		}

		/// <summary>
		/// Closes the WebSocket connection automatically in response to some invalid data from the remote host
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="ex">The exception (for logging)</param>
		internal async Task CloseOutputTimeoutAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, Exception ex)
		{
			var timespan = TimeSpan.FromSeconds(3);
			Events.Log.CloseOutputAutoTimeout(this.ID, closeStatus, closeStatusDescription, ex.ToString());

			try
			{
				using (var cts = new CancellationTokenSource(timespan))
				{
					await this.CloseOutputAsync(closeStatus, (closeStatusDescription ?? "") + (this._includeExceptionInCloseResponse ? "\r\n\r\n" + ex.ToString() : ""), cts.Token).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException)
			{
				// do not throw an exception because that will mask the original exception
				Events.Log.CloseOutputAutoTimeoutCancelled(this.ID, (int)timespan.TotalSeconds, closeStatus, closeStatusDescription, ex.ToString());
			}
			catch (Exception closeException)
			{
				// do not throw an exception because that will mask the original exception
				Events.Log.CloseOutputAutoTimeoutError(this.ID, closeException.ToString(), closeStatus, closeStatusDescription, ex.ToString());
			}
		}

		/// <summary>
		/// Closes the WebSocket connection with time-out cancellation token
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="cancellationToken">The time-out cancellation token</param>
		/// <param name="onCanceled">The action to fire when cancellation token is raised</param>
		/// <param name="onError">The action to fire when got error</param>
		internal async Task CloseOutputTimeoutAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, CancellationToken cancellationToken, Action onCanceled = null, Action<Exception> onError = null)
		{
			try
			{
				await this.CloseOutputAsync(closeStatus, closeStatusDescription, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				onCanceled?.Invoke();
			}
			catch (Exception ex)
			{
				onError?.Invoke(ex);
			}
		}

		/// <summary>
		/// Aborts the WebSocket without sending a Close frame
		/// </summary>
		public override void Abort()
		{
			this._state = WebSocketState.Aborted;
			this._readingCTS.Cancel();
		}
		#endregion

		#region Dispose
		/// <summary>
		/// Dispose will send a close frame if the connection is still open
		/// </summary>
		public override void Dispose()
		{
			this.DisposeAsync().Wait();
		}

		/// <summary>
		/// Dispose will send a close frame if the connection is still open
		/// </summary>
		/// <param name="closeStatus"></param>
		/// <param name="closeStatusDescription"></param>
		/// <param name="cancellationToken"></param>
		internal async Task DisposeAsync(WebSocketCloseStatus closeStatus = WebSocketCloseStatus.EndpointUnavailable, string closeStatusDescription = "Service is unavailable", CancellationToken cancellationToken = default(CancellationToken))
		{
			if (!this._disposed)
			{
				Events.Log.WebSocketDispose(this.ID, this._state);
				if (this._state == WebSocketState.Open)
					using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token))
					{
						await this.CloseOutputTimeoutAsync(
							closeStatus,
							closeStatusDescription,
							cts.Token,
							() => Events.Log.WebSocketDisposeCloseTimeout(this.ID, this._state),
							(ex) => Events.Log.WebSocketDisposeError(this.ID, this._state, ex.ToString())
						).ConfigureAwait(false);
					}
				this._readingCTS.Cancel();
				this._stream.Close();
				Events.Log.WebSocketDispose(this.ID, this._state);
				this._disposed = true;
			}
		}

		~WebSocket()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}
		#endregion

		#region Helpers
		/// <summary>
		/// Turns a spec websocket frame opcode into a WebSocketMessageType
		/// </summary>
		WebSocketOpCode GetOpCode(WebSocketMessageType messageType)
		{
			if (this._isContinuationFrame)
				return WebSocketOpCode.ContinuationFrame;

			switch (messageType)
			{
				case WebSocketMessageType.Binary:
					return WebSocketOpCode.BinaryFrame;

				case WebSocketMessageType.Text:
					return WebSocketOpCode.TextFrame;

				case WebSocketMessageType.Close:
					throw new NotSupportedException("Cannot use Send function to send a close frame. Use Close function.");

				default:
					throw new NotSupportedException($"MessageType {messageType} not supported");
			}
		}

		/// <summary>
		/// As per the spec, write the close status followed by the close reason
		/// </summary>
		/// <param name="closeStatus">The close status</param>
		/// <param name="closeStatusDescription">Optional extra close details</param>
		/// <returns>The payload to sent in the close frame</returns>
		ArraySegment<byte> BuildClosePayload(WebSocketCloseStatus closeStatus, string closeStatusDescription)
		{
			var buffer = ((ushort)closeStatus).ToBytes();
			Array.Reverse(buffer); // network byte order (big endian)
			return string.IsNullOrWhiteSpace(closeStatusDescription)
				? buffer.ToArraySegment()
				: buffer.Concat(closeStatusDescription.ToBytes()).ToArraySegment();
		}

		/// <summary>
		/// Called when a Close frame is received
		/// Send a response close frame if applicable
		/// </summary>
		async Task<WebSocketReceiveResult> RespondToCloseFrameAsync(WebSocketFrame frame, ArraySegment<byte> buffer, CancellationToken cancellationToken)
		{
			this._closeStatus = frame.CloseStatus;
			this._closeStatusDescription = frame.CloseStatusDescription;

			if (this._state == WebSocketState.CloseSent)
			{
				// this is a response to close handshake initiated by this instance
				this._state = WebSocketState.Closed;
				Events.Log.CloseHandshakeComplete(this.ID);
			}
			else if (this._state == WebSocketState.Open)
			{
				// this is in response to a close handshake initiated by the remote instance
				var closePayload = new ArraySegment<byte>(buffer.Array, buffer.Offset, frame.Count);
				this._state = WebSocketState.CloseReceived;
				Events.Log.CloseHandshakeRespond(this.ID, frame.CloseStatus, frame.CloseStatusDescription);

				using (var stream = this._recycledStreamFactory())
				{
					FrameReaderWriter.Write(WebSocketOpCode.ConnectionClose, closePayload, stream, true, this.IsClient);
					Events.Log.SendingFrame(this.ID, WebSocketOpCode.ConnectionClose, true, closePayload.Count, false);
					await this.WriteStreamToNetworkAsync(stream, cancellationToken).ConfigureAwait(false);
				}
			}
			else
				Events.Log.CloseFrameReceivedInUnexpectedState(this.ID, this._state, frame.CloseStatus, frame.CloseStatusDescription);

			return new WebSocketReceiveResult(frame.Count, WebSocketMessageType.Close, frame.IsFinBitSet, frame.CloseStatus, frame.CloseStatusDescription);
		}

		/// <summary>
		/// Puts data on the wire
		/// </summary>
		/// <param name="stream">The stream to read data from</param>
		async Task WriteStreamToNetworkAsync(MemoryStream stream, CancellationToken cancellationToken)
		{
			// avoid calling ToArray on the MemoryStream because it allocates a new byte array on the heap
			// we avoid this by attempting to access the internal memory stream buffer
			// this works with supported streams like the recyclable memory stream and writable memory streams
			if (!stream.TryGetBuffer(out ArraySegment<byte> buffer))
			{
				if (!this._tryGetBufferFailureLogged)
				{
					Events.Log.TryGetBufferNotSupported(this.ID, stream.GetType()?.ToString());
					this._tryGetBufferFailureLogged = true;
				}

				// internal buffer not suppoted, fall back to ToArray()
				buffer = stream.ToArray().ToArraySegment();
			}
			else
				buffer = new ArraySegment<byte>(buffer.Array, buffer.Offset, (int)stream.Position);

			// add into queue and check pending write operations
			this._buffers.Enqueue(buffer);
			if (this._writting)
			{
				var logger = Logger.CreateLogger<WebSocket>();
				if (logger.IsEnabled(LogLevel.Debug))
					logger.LogWarning($"Pending write operations => {this._buffers.Count:#,##0} ({this.ID} @ {this.RemoteEndPoint})");
				return;
			}

			// put data to wire
			this._writting = true;
			try
			{
				while (this._buffers.Count > 0)
					if (this._buffers.TryDequeue(out buffer))
						await this._stream.WriteAsync(buffer.Array, buffer.Offset, buffer.Count, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception)
			{
				throw;
			}
			finally
			{
				this._writting = false;
			}
		}
		#endregion

	}
}