// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Bond.Comm.Epoxy
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Bond.Comm.Layers;
    using Bond.Comm.Service;
    using Bond.IO.Safe;
    using Bond.Protocols;

    public class EpoxyConnection : Connection, IRequestResponseConnection, IEventConnection
    {
        static readonly EpoxyConfig EmptyConfig = new EpoxyConfig();

        private enum ConnectionType
        {
            Client,
            Server
        }

        [Flags]
        private enum State
        {
            None = 0,
            Created = 0x01,
            ClientSendConfig = 0x02,
            ClientExpectConfig = 0x04,
            ServerExpectConfig = 0x08,
            ServerSendConfig = 0x10,
            Connected = 0x20,
            SendProtocolError = 0x40,
            Disconnecting = 0x80,
            Disconnected = 0x100,
            All = Created | ClientSendConfig | ClientExpectConfig | ServerExpectConfig | ServerSendConfig | Connected | SendProtocolError | Disconnecting | Disconnected,
        }

        readonly ConnectionType connectionType;

        readonly EpoxyTransport parentTransport;
        readonly EpoxyListener parentListener;
        readonly ServiceHost serviceHost;

        readonly EpoxySocket netSocket;

        readonly ResponseMap responseMap;

        State state;
        readonly TaskCompletionSource<bool> startTask;
        readonly TaskCompletionSource<bool> stopTask;
        readonly CancellationTokenSource shutdownTokenSource;

        long prevConversationId;

        ProtocolErrorCode protocolError;
        Error errorDetails;

        // this member is used to capture any handshake errors
        ProtocolError handshakeError;

        readonly ConnectionMetrics connectionMetrics = new ConnectionMetrics();
        Stopwatch duration;

        readonly Logger logger;
        readonly Metrics metrics;

        private EpoxyConnection(
            ConnectionType connectionType,
            EpoxyTransport parentTransport,
            EpoxyListener parentListener,
            ServiceHost serviceHost,
            Socket socket,
            Logger logger,
            Metrics metrics)
        {
            Debug.Assert(parentTransport != null);
            Debug.Assert(connectionType != ConnectionType.Server || parentListener != null, "Server connections must have a listener");
            Debug.Assert(serviceHost != null);
            Debug.Assert(socket != null);

            this.connectionType = connectionType;

            this.parentTransport = parentTransport;
            this.parentListener = parentListener;
            this.serviceHost = serviceHost;

            netSocket = new EpoxySocket(socket, logger);

            // cache these so we can use them after the socket has been shutdown
            LocalEndPoint = (IPEndPoint) socket.LocalEndPoint;
            RemoteEndPoint = (IPEndPoint) socket.RemoteEndPoint;

            responseMap = new ResponseMap();

            state = State.Created;
            startTask = new TaskCompletionSource<bool>();
            stopTask = new TaskCompletionSource<bool>();
            shutdownTokenSource = new CancellationTokenSource();

            // start at -1 or 0 so the first conversation ID is 1 or 2.
            prevConversationId = (connectionType == ConnectionType.Client) ? -1 : 0;

            connectionMetrics.connection_id = Guid.NewGuid().ToString();
            connectionMetrics.local_endpoint = LocalEndPoint.ToString();
            connectionMetrics.remote_endpoint = RemoteEndPoint.ToString();

            this.logger = logger;
            this.metrics = metrics;
        }

        internal static EpoxyConnection MakeClientConnection(
            EpoxyTransport parentTransport,
            Socket clientSocket,
            Logger logger,
            Metrics metrics)
        {
            const EpoxyListener parentListener = null;

            return new EpoxyConnection(
                ConnectionType.Client,
                parentTransport,
                parentListener,
                new ServiceHost(logger, metrics),
                clientSocket,
                logger,
                metrics);
        }

        internal static EpoxyConnection MakeServerConnection(
            EpoxyTransport parentTransport,
            EpoxyListener parentListener,
            ServiceHost serviceHost,
            Socket socket,
            Logger logger,
            Metrics metrics)
        {
            return new EpoxyConnection(
                ConnectionType.Server,
                parentTransport,
                parentListener,
                serviceHost,
                socket,
                logger,
                metrics);
        }

        /// <summary>
        /// Get this connection's local endpoint.
        /// </summary>
        public IPEndPoint LocalEndPoint { get; private set; }

        /// <summary>
        /// Get this connection's remote endpoint.
        /// </summary>
        public IPEndPoint RemoteEndPoint { get; private set; }

        public override string ToString()
        {
            return $"{nameof(EpoxyConnection)}(local: {LocalEndPoint}, remote: {RemoteEndPoint})";
        }

        internal static Frame MessageToFrame(ulong conversationId, string methodName, PayloadType type, IMessage payload, IBonded layerData, Logger logger)
        {
            var frame = new Frame(logger);

            {
                var headers = new EpoxyHeaders
                {
                    conversation_id = conversationId,
                    payload_type = type,
                    method_name = methodName ?? string.Empty, // method_name is not nullable
                };

                if (payload.IsError)
                {
                    headers.error_code = payload.Error.Deserialize<Error>().error_code;
                }
                else
                {
                    headers.error_code = (int)ErrorCode.OK;
                }

                const int initialHeaderBufferSize = 150;
                var outputBuffer = new OutputBuffer(initialHeaderBufferSize);
                var fastWriter = new FastBinaryWriter<OutputBuffer>(outputBuffer);
                Serialize.To(fastWriter, headers);

                frame.Add(new Framelet(FrameletType.EpoxyHeaders, outputBuffer.Data));
            }

            if (layerData != null)
            {
                const int initialLayerDataBufferSize = 150;
                var outputBuffer = new OutputBuffer(initialLayerDataBufferSize);
                var compactWriter = new CompactBinaryWriter<OutputBuffer>(outputBuffer);
                // TODO: See TODO below about issues with IBonded Marshal.TO(...)
                compactWriter.WriteVersion();
                layerData.Serialize(compactWriter);
                frame.Add(new Framelet(FrameletType.LayerData, outputBuffer.Data));
            }

            {
                var userData = payload.IsError ? (IBonded)payload.Error : (IBonded)payload.RawPayload;


                const int initialPayloadBufferSize = 1024;
                var outputBuffer = new OutputBuffer(initialPayloadBufferSize);
                var compactWriter = new CompactBinaryWriter<OutputBuffer>(outputBuffer);
                // TODO: marshal dies on IBonded Marshal.To(compactWriter, request)
                // understand more deeply why and consider fixing
                compactWriter.WriteVersion();
                userData.Serialize(compactWriter);

                frame.Add(new Framelet(FrameletType.PayloadData, outputBuffer.Data));
            }

            return frame;
        }

        internal static Frame MakeConfigFrame(Logger logger)
        {
            var outputBuffer = new OutputBuffer(1);
            var fastWriter = new FastBinaryWriter<OutputBuffer>(outputBuffer);
            Serialize.To(fastWriter, EmptyConfig);

            var frame = new Frame(1, logger);
            frame.Add(new Framelet(FrameletType.EpoxyConfig, outputBuffer.Data));
            return frame;
        }

        internal static Frame MakeProtocolErrorFrame(ProtocolErrorCode errorCode, Error details, Logger logger)
        {
            var protocolError = new ProtocolError
            {
                error_code = errorCode,
                details = details == null ? null : new Bonded<Error>(details)
            };

            var outputBuffer = new OutputBuffer(16);
            var fastWriter = new FastBinaryWriter<OutputBuffer>(outputBuffer);
            Serialize.To(fastWriter, protocolError);

            var frame = new Frame(1, logger);
            frame.Add(new Framelet(FrameletType.ProtocolError, outputBuffer.Data));
            return frame;
        }

        private async Task<IMessage> SendRequestAsync<TPayload>(string methodName, IMessage<TPayload> request)
        {
            var conversationId = AllocateNextConversationId();

            var sendContext = new EpoxySendContext(this);

            IBonded layerData = null;
            ILayerStack layerStack;
            Error layerError = parentTransport.GetLayerStack(out layerStack);

            if (layerError == null)
            {
                layerError = LayerStackUtils.ProcessOnSend(
                        layerStack, MessageType.Request, sendContext, out layerData, logger);
            }

            if (layerError != null)
            {
                logger.Site().Error("{0} Sending request {1}/{2} failed due to layer error (Code: {3}, Message: {4}).",
                            this, conversationId, methodName, layerError.error_code, layerError.message);
                return Message.FromError(layerError);
            }

            var frame = MessageToFrame(conversationId, methodName, PayloadType.Request, request, layerData, logger);

            logger.Site().Debug("{0} Sending request {1}/{2}.", this, conversationId, methodName);
            var responseTask = responseMap.Add(conversationId, layerStack);

            bool wasSent = await SendFrameAsync(frame);
            logger.Site().Debug("{0} Sending request {1}/{2} {3}.",
                this, conversationId, methodName, wasSent ? "succeeded" : "failed");

            if (!wasSent)
            {
                bool wasCompleted = responseMap.Complete(
                    conversationId,
                    Message.FromError(new Error
                    {
                        error_code = (int) ErrorCode.TransportError,
                        message = "Request could not be sent"
                    }));

                if (!wasCompleted)
                {
                    logger.Site().Information("{0} Unsuccessfully sent request {1}/{2} still received response.",
                        this, conversationId, methodName);
                }
            }

            return await responseTask;
        }

        private async Task SendReplyAsync(ulong conversationId, IMessage response, ILayerStack layerStack)
        {
            var sendContext = new EpoxySendContext(this);
            IBonded layerData;
            Error layerError = LayerStackUtils.ProcessOnSend(
                    layerStack, MessageType.Response, sendContext, out layerData, logger);

            // If there was a layer error, replace the response with the layer error
            if (layerError != null)
            {
                logger.Site().Error("{0} Sending reply for conversation ID {1} failed due to layer error (Code: {2}, Message: {3}).",
                            this, conversationId, layerError.error_code, layerError.message);

                // Set layer error as result of this Bond method call, replacing original response.
                // Since this error will be returned to client, cleanse out internal server error details, if any.
                response = Message.FromError(Errors.CleanseInternalServerError(layerError));
            }

            var frame = MessageToFrame(conversationId, null, PayloadType.Response, response, layerData, logger);
            logger.Site().Debug("{0} Sending reply for conversation ID {1}.", this, conversationId);

            bool wasSent = await SendFrameAsync(frame);
            logger.Site().Debug("{0} Sending reply for conversation ID {1} {2}.",
                this, conversationId, wasSent ? "succeedeed" : "failed");
        }

        private async Task<bool> SendFrameAsync(Frame frame)
        {
            try
            {
                Stream networkStream = netSocket.NetworkStream;

                await netSocket.WriteLock.WaitAsync();
                try
                {
                    await frame.WriteAsync(networkStream);
                }
                finally
                {
                    netSocket.WriteLock.Release();
                }

                await networkStream.FlushAsync();
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
            {
                logger.Site().Error(ex, "{0} While writing a Frame to the network: {1}", this, ex.Message);
                return false;
            }
        }

        internal async Task SendEventAsync(string methodName, IMessage message)
        {
            var conversationId = AllocateNextConversationId();

            var sendContext = new EpoxySendContext(this);
            IBonded layerData =  null;
            ILayerStack layerStack;
            Error layerError = parentTransport.GetLayerStack(out layerStack);

            if (layerError == null)
            {
                layerError = LayerStackUtils.ProcessOnSend(
                        layerStack, MessageType.Event, sendContext, out layerData, logger);
            }

            if (layerError != null)
            {
                logger.Site().Error("{0} Sending event {1}/{2} failed due to layer error (Code: {3}, Message: {4}).",
                    this, conversationId, methodName, layerError.error_code, layerError.message);
                return;
            }

            var frame = MessageToFrame(conversationId, methodName, PayloadType.Event, message, layerData, logger);

            logger.Site().Debug("{0} Sending event {1}/{2}.", this, conversationId, methodName);

            bool wasSent = await SendFrameAsync(frame);
            logger.Site().Debug("{0} Sending event {1}/{2} {3}.",
                this, conversationId, methodName, wasSent ? "succeeded" : "failed");
        }

        internal Task StartAsync()
        {
            EnsureCorrectState(State.Created);
            duration = Stopwatch.StartNew();
            Task.Run((Func<Task>)ConnectionLoop);
            return startTask.Task;
        }

        private void EnsureCorrectState(State allowedStates, [CallerMemberName] string methodName = "<unknown>")
        {
            if ((state & allowedStates) == 0)
            {
                var message = $"Connection ({this}) is not in the correct state for the requested operation ({methodName}). Current state: {state} Allowed states: {allowedStates}";
                throw new InvalidOperationException(message);
            }
        }

        private ulong AllocateNextConversationId()
        {
            // Interlocked.Add() handles overflow by wrapping, not throwing.
            var newConversationId = Interlocked.Add(ref prevConversationId, 2);
            if (newConversationId < 0)
            {
                throw new EpoxyProtocolErrorException("Exhausted conversation IDs");
            }
            return unchecked((ulong)newConversationId);
        }

        private async Task ConnectionLoop()
        {
            while (true)
            {
                State nextState;

                try
                {
                    if (state == State.Disconnected)
                    {
                        break; // while loop
                    }

                    switch (state)
                    {
                        case State.Created:
                            nextState = DoCreated();
                            break;

                        case State.ClientSendConfig:
                        case State.ServerSendConfig:
                            nextState = await DoSendConfigAsync();
                            break;

                        case State.ClientExpectConfig:
                        case State.ServerExpectConfig:
                            nextState = await DoExpectConfigAsync();
                            break;

                        case State.Connected:
                            // signal after state change to prevent races with
                            // EnsureCorrectState
                            startTask.SetResult(true);
                            nextState = await DoConnectedAsync();
                            break;

                        case State.SendProtocolError:
                            nextState = await DoSendProtocolErrorAsync();
                            break;

                        case State.Disconnecting:
                            nextState = DoDisconnect();
                            break;

                        case State.Disconnected: // we should never enter this switch in the Disconnected state
                        default:
                            logger.Site().Error("{0} Unexpected connection state: {1}", this, state);
                            protocolError = ProtocolErrorCode.INTERNAL_ERROR;
                            nextState = State.SendProtocolError;
                            break;
                    }
                }
                catch (Exception ex) when (state != State.Disconnecting && state != State.Disconnected)
                {
                    logger.Site().Error(ex, "{0} Unhandled exception. Current state: {1}", this, state);

                    // we're in a state where we can attempt to disconnect
                    protocolError = ProtocolErrorCode.INTERNAL_ERROR;
                    nextState = State.Disconnecting;
                }
                catch (Exception ex)
                {
                    logger.Site().Error(ex, "{0} Unhandled exception during shutdown. Abandoning connection. Current state: {1}",
                        this, state);
                    break; // the while loop
                }

                state = nextState;
            } // while (true)

            if (state != State.Disconnected)
            {
                logger.Site().Information("{0} Abandoning connection. Current state: {1}", this, state);
            }

            DoDisconnected();
        }

        private State DoCreated()
        {
            State result;

            if (connectionType == ConnectionType.Server)
            {
                var args = new ConnectedEventArgs(this);
                Error disconnectError = parentListener.InvokeOnConnected(args);

                if (disconnectError == null)
                {
                    result = State.ServerExpectConfig;
                }
                else
                {
                    logger.Site().Information("{0} Rejecting connection because {1}:{2}",
                        this, disconnectError.error_code, disconnectError.message);

                    protocolError = ProtocolErrorCode.CONNECTION_REJECTED;
                    errorDetails = disconnectError;
                    result = State.SendProtocolError;
                }
            }
            else
            {
                result = State.ClientSendConfig;
            }

            return result;
        }

        private async Task<State> DoSendConfigAsync()
        {
            Frame emptyConfigFrame = MakeConfigFrame(logger);
            await SendFrameAsync(emptyConfigFrame);
            return (connectionType == ConnectionType.Server ? State.Connected : State.ClientExpectConfig);
        }

        private async Task<State> DoExpectConfigAsync()
        {
            Stream networkStream = netSocket.NetworkStream;
            Frame frame = await Frame.ReadAsync(networkStream, shutdownTokenSource.Token, logger);
            if (frame == null)
            {
                logger.Site().Information("{0} EOS encountered while waiting for config, so disconnecting.", this);
                return State.Disconnecting;
            }

            var result = EpoxyProtocol.Classify(frame, logger);
            switch (result.Disposition)
            {
                case EpoxyProtocol.FrameDisposition.ProcessConfig:
                    // we don't actually use the config yet
                    return (connectionType == ConnectionType.Server ? State.ServerSendConfig : State.Connected);

                case EpoxyProtocol.FrameDisposition.HandleProtocolError:
                    // we got a protocol error while we expected config
                    handshakeError = result.Error;
                    return State.Disconnecting;

                case EpoxyProtocol.FrameDisposition.HangUp:
                    return State.Disconnecting;

                default:
                    protocolError = result.ErrorCode ?? ProtocolErrorCode.PROTOCOL_VIOLATED;
                    logger.Site().Error("{0} Unsupported FrameDisposition {1} when waiting for config. ErrorCode: {2})",
                        this, result.Disposition, protocolError);
                    return State.SendProtocolError;
            }
        }

        private async Task<State> DoConnectedAsync()
        {
            while (!shutdownTokenSource.IsCancellationRequested)
            {
                Frame frame;

                try
                {
                    Stream networkStream = netSocket.NetworkStream;
                    frame = await Frame.ReadAsync(networkStream, shutdownTokenSource.Token, logger);
                    if (frame == null)
                    {
                        logger.Site().Information("{0} EOS encountered, so disconnecting.", this);
                        return State.Disconnecting;
                    }
                }
                catch (EpoxyProtocolErrorException pex)
                {
                    logger.Site().Error(pex, "{0} Protocol error encountered.", this);
                    protocolError = ProtocolErrorCode.PROTOCOL_VIOLATED;
                    return State.SendProtocolError;
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
                {
                    logger.Site().Error(ex, "{0} IO error encountered.", this);
                    return State.Disconnecting;
                }


                var result = EpoxyProtocol.Classify(frame, logger);
                switch (result.Disposition)
                {
                    case EpoxyProtocol.FrameDisposition.DeliverRequestToService:
                    {
                        State? nextState = DispatchRequest(result.Headers, result.Payload, result.LayerData);
                        if (nextState.HasValue)
                        {
                            return nextState.Value;
                        }
                        else
                        {
                            // continue the read loop
                            break;
                        }
                    }

                    case EpoxyProtocol.FrameDisposition.DeliverResponseToProxy:
                        DispatchResponse(result.Headers, result.Payload, result.LayerData);
                        break;

                    case EpoxyProtocol.FrameDisposition.DeliverEventToService:
                        DispatchEvent(result.Headers, result.Payload, result.LayerData);
                        break;

                    case EpoxyProtocol.FrameDisposition.SendProtocolError:
                        protocolError = result.ErrorCode ?? ProtocolErrorCode.INTERNAL_ERROR;
                        return State.SendProtocolError;

                    case EpoxyProtocol.FrameDisposition.HandleProtocolError:
                    case EpoxyProtocol.FrameDisposition.HangUp:
                        return State.Disconnecting;

                    default:
                        logger.Site().Error("{0} Unsupported FrameDisposition {1}", this, result.Disposition);
                        protocolError = ProtocolErrorCode.INTERNAL_ERROR;
                        return State.SendProtocolError;
                }
            }

            // shutdown requested between reading frames
            return State.Disconnecting;
        }

        private async Task<State> DoSendProtocolErrorAsync()
        {
            ProtocolErrorCode errorCode = protocolError;
            Error details = errorDetails;

            var frame = MakeProtocolErrorFrame(errorCode, details, logger);
            logger.Site().Debug("{0} Sending protocol error with code {1} and details {2}.",
                this, errorCode, details == null ? "<null>" : details.error_code + details.message);

            bool wasSent = await SendFrameAsync(frame);
            logger.Site().Debug(
                "{0} Sending protocol error with code {1} {2}.",
                this, errorCode, wasSent ? "succeeded" : "failed");

            return State.Disconnecting;
        }

        private State DoDisconnect()
        {
            logger.Site().Debug("{0} Shutting down.", this);

            netSocket.Shutdown();

            if (connectionType == ConnectionType.Server)
            {
                var args = new DisconnectedEventArgs(this, errorDetails);
                parentListener.InvokeOnDisconnected(args);
            }

            responseMap.Shutdown();

            return State.Disconnected;
        }

        private void DoDisconnected()
        {
            // We signal the start and stop tasks after the state change to
            // prevent races with EnsureCorrectState

            if (handshakeError != null)
            {
                var pex = new EpoxyProtocolErrorException(
                    "Connection was rejected",
                    innerException: null,
                    details: handshakeError.details);
                startTask.TrySetException(pex);
            }
            else
            {
                // the connection got started but then got shutdown shortly after
                startTask.TrySetResult(true);
            }

            stopTask.SetResult(true);

            duration.Stop();
            connectionMetrics.duration_millis = (float) duration.Elapsed.TotalMilliseconds;
            metrics.Emit(connectionMetrics);
        }

        private State? DispatchRequest(EpoxyHeaders headers, ArraySegment<byte> payload, ArraySegment<byte> layerData)
        {
            if (headers.error_code != (int)ErrorCode.OK)
            {
                logger.Site().Error("{0} Received request with a non-zero error code. Conversation ID: {1}",
                    this, headers.conversation_id);
                protocolError = ProtocolErrorCode.PROTOCOL_VIOLATED;
                return State.SendProtocolError;
            }

            Task.Run(async () =>
            {
                IMessage request = Message.FromPayload(Unmarshal.From(payload));

                var receiveContext = new EpoxyReceiveContext(this);

                IBonded bondedLayerData = (layerData.Array == null) ? null : Unmarshal.From(layerData);

                ILayerStack layerStack;
                Error layerError = parentTransport.GetLayerStack(out layerStack);

                if (layerError == null)
                {
                    layerError = LayerStackUtils.ProcessOnReceive(
                            layerStack, MessageType.Request, receiveContext, bondedLayerData, logger);
                }

                IMessage result;

                if (layerError == null)
                {
                    result = await serviceHost.DispatchRequest(headers.method_name, receiveContext, request,
                            connectionMetrics);
                }
                else
                {
                    logger.Site().Error("{0} Receiving request {1}/{2} failed due to layer error (Code: {3}, Message: {4}).",
                        this, headers.conversation_id, headers.method_name,
                        layerError.error_code, layerError.message);

                    // Set layer error as result of this Bond method call and do not dispatch to method.
                    // Since this error will be returned to client, cleanse out internal server error details, if any.
                    result = Message.FromError(Errors.CleanseInternalServerError(layerError));
                }

                await SendReplyAsync(headers.conversation_id, result, layerStack);
            });

            // no state change needed
            return null;
        }

        private void DispatchResponse(EpoxyHeaders headers, ArraySegment<byte> payload, ArraySegment<byte> layerData)
        {
            IMessage response;
            if (headers.error_code != (int)ErrorCode.OK)
            {
                response = Message.FromError(Unmarshal<Error>.From(payload));
            }
            else
            {
                response = Message.FromPayload(Unmarshal.From(payload));
            }

            TaskCompletionSource<IMessage> tcs = responseMap.TakeTaskCompletionSource(headers.conversation_id);
            if (tcs == null)
            {
                logger.Site().Error("{0} Response for unmatched request. Conversation ID: {1}",
                                 this, headers.conversation_id);
                return;
            }

            Task.Run(() =>
            {
                var receiveContext = new EpoxyReceiveContext(this);

                IBonded bondedLayerData = (layerData.Array == null) ? null : Unmarshal.From(layerData);

                ILayerStack layerStack = tcs.Task.AsyncState as ILayerStack;

                Error layerError = LayerStackUtils.ProcessOnReceive(layerStack, MessageType.Response, receiveContext, bondedLayerData, logger);

                if (layerError != null)
                {
                    logger.Site().Error("{0} Receiving response {1}/{2} failed due to layer error (Code: {3}, Message: {4}).",
                                     this, headers.conversation_id, headers.method_name,
                                     layerError.error_code, layerError.message);
                    response = Message.FromError(layerError);
                }

                tcs.SetResult(response);
            });
        }

        private void DispatchEvent(EpoxyHeaders headers, ArraySegment<byte> payload, ArraySegment<byte> layerData)
        {
            if (headers.error_code != (int)ErrorCode.OK)
            {
                logger.Site().Error("{0} Received event with a non-zero error code. Conversation ID: {1}",
                    this, headers.conversation_id);
                return;
            }

            Task.Run(async () =>
            {
                IMessage request = Message.FromPayload(Unmarshal.From(payload));

                var receiveContext = new EpoxyReceiveContext(this);

                IBonded bondedLayerData = (layerData.Array == null) ? null : Unmarshal.From(layerData);
                ILayerStack layerStack;
                Error layerError = parentTransport.GetLayerStack(out layerStack);

                if (layerError == null)
                {
                    layerError = LayerStackUtils.ProcessOnReceive(
                            layerStack, MessageType.Event, receiveContext, bondedLayerData, logger);
                }

                if (layerError != null)
                {
                    logger.Site().Error("{0}: Receiving event {1}/{2} failed due to layer error (Code: {3}, Message: {4}).",
                                this, headers.conversation_id, headers.method_name,
                                layerError.error_code, layerError.message);
                    return;
                }

                await serviceHost.DispatchEvent(headers.method_name, receiveContext, request, connectionMetrics);
            });
        }

        public override Task StopAsync()
        {
            EnsureCorrectState(State.All);
            shutdownTokenSource.Cancel();
            netSocket.Shutdown();

            return stopTask.Task;
        }

        public async Task<IMessage<TResponse>> RequestResponseAsync<TRequest, TResponse>(string methodName, IMessage<TRequest> message, CancellationToken ct)
        {
            EnsureCorrectState(State.Connected);

            // TODO: cancellation
            IMessage response = await SendRequestAsync(methodName, message);
            return response.Convert<TResponse>();
        }

        public Task FireEventAsync<TPayload>(string methodName, IMessage<TPayload> message)
        {
            EnsureCorrectState(State.Connected);
            return SendEventAsync(methodName, message);
        }

        /// <summary>
        /// Epoxy-private wrapper around <see cref="Socket"/>. Provides idempotent shutdown.
        /// </summary>
        private class EpoxySocket
        {
            Socket socket;
            NetworkStream stream;
            int isShutdown;
            Logger logger;

            public EpoxySocket(Socket sock, Logger logger)
            {
                socket = sock;
                stream = new NetworkStream(sock, ownsSocket: false);
                WriteLock = new SemaphoreSlim(1, 1);
                isShutdown = 0;
                this.logger = logger;
            }

            public Stream NetworkStream
            {
                get
                {
                    if (isShutdown != 0)
                    {
                        throw new ObjectDisposedException(nameof(EpoxySocket));
                    }

                    return stream;
                }
            }

            // It looks like we don't need to .Dispose this SemaphoreSlim. The
            // current implementation of SemaphoreSlim only does interesting
            // stuff during .Dispose if there's an allocated
            // AvailableWaitHandle. We never call that, so there shouldn't be
            // anything needing disposal. If we do end up allocating a wait
            // handle somehow, its finalizer will save us.
            public SemaphoreSlim WriteLock { get; }

            public void Shutdown()
            {
                int oldIsShutdown = Interlocked.CompareExchange(ref isShutdown, 1, 0);
                if (oldIsShutdown == 0)
                {
                    // we are responsible for shutdown
                    try
                    {
                        stream.Dispose();

                        try
                        {
                            socket.Shutdown(SocketShutdown.Send);
                            socket.Close();
                        }
                        catch (ObjectDisposedException)
                        {
                            // ignore, as we're shutting down anyway
                        }

                        // We cannot call socket.Disconnect, as that will block
                        // for longer than we want. So, we just forcible close
                        // the socket with Dispose
                        socket.Dispose();
                    }
                    catch (Exception ex) when (ex is IOException || ex is SocketException)
                    {
                        logger.Site().Error(ex, "Exception during connection shutdown");
                    }

                    stream = null;
                    socket = null;
                }
            }
        }
    }
}
