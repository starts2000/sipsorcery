﻿//-----------------------------------------------------------------------------
// Filename: Program.cs
//
// Description: An example WebRTC server application that serves a media stream
// sourced from an MP4 file to a WebRTC enabled browser.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
// 
// History:
// 20 Jan 2020	Aaron Clauson	Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Codecs;
using Serilog;
using SIPSorcery.Net;
using SIPSorceryMedia;
using WebSocketSharp;
using WebSocketSharp.Net.WebSockets;
using WebSocketSharp.Server;

namespace WebRTCServer
{
    public class SDPExchange : WebSocketBehavior
    {
        public RTCPeerConnection PeerConnection;

        public event Func<WebSocketContext, Task<RTCPeerConnection>> WebSocketOpened;
        public event Func<RTCPeerConnection, string, Task> OnMessageReceived;

        public SDPExchange()
        { }

        protected override async void OnMessage(MessageEventArgs e)
        {
            await OnMessageReceived(PeerConnection, e.Data);
        }

        protected override async void OnOpen()
        {
            base.OnOpen();
            PeerConnection = await WebSocketOpened(this.Context);
        }

        protected override void OnClose(CloseEventArgs e)
        {
            base.OnClose(e);
            PeerConnection.Close("remote party close");
        }
    }

    class Program
    {
        private static string MP4_FILE_PATH = "media/big_buck_bunny.mp4";
        private const int VP8_TIMESTAMP_SPACING = 3000;
        private const int VP8_PAYLOAD_TYPE_ID = 100;
        private const string WEBSOCKET_CERTIFICATE_PATH = "certs/localhost.pfx";
        private const string DTLS_CERTIFICATE_PATH = "certs/localhost.pem";
        private const string DTLS_KEY_PATH = "certs/localhost_key.pem";
        private const string DTLS_CERTIFICATE_FINGERPRINT = "sha-256 C6:ED:8C:9D:06:50:77:23:0A:4A:D8:42:68:29:D0:70:2F:BB:C7:72:EC:98:5C:62:07:1B:0C:5D:CB:CE:BE:CD";
        private const int WEBSOCKET_PORT = 8081;
        private const int TEST_DTLS_HANDSHAKE_TIMEOUT = 10000;

        private static Microsoft.Extensions.Logging.ILogger logger = SIPSorcery.Sys.Log.Logger;

        private static WebSocketServer _webSocketServer;
        private static MediaSource _mediaSource;
        private static bool _isSampling = false;
        private static VpxEncoder _vpxEncoder;
        private static uint _vp8Timestamp;
        private static uint _mulawTimestamp;

        private delegate void MediaSampleReadyDelegate(SDPMediaTypesEnum mediaType, uint timestamp, byte[] sample);
        private static event MediaSampleReadyDelegate OnMediaSampleReady;

        static void Main()
        {
            Console.WriteLine("WebRTC Server Sample Program");
            Console.WriteLine("Press ctrl-c to exit.");

            // Plumbing code to facilitate a graceful exit.
            CancellationTokenSource exitCts = new CancellationTokenSource(); // Cancellation token to stop the SIP transport and RTP stream.
            ManualResetEvent exitMre = new ManualResetEvent(false);

            AddConsoleLogger();

            if (!File.Exists(MP4_FILE_PATH))
            {
                throw new ApplicationException($"The media file at does not exist at {MP4_FILE_PATH}.");
            }
            else if (!File.Exists(DTLS_CERTIFICATE_PATH))
            {
                throw new ApplicationException($"The DTLS certificate file could not be found at {DTLS_CERTIFICATE_PATH}.");
            }
            else if (!File.Exists(DTLS_KEY_PATH))
            {
                throw new ApplicationException($"The DTLS key file could not be found at {DTLS_KEY_PATH}.");
            }

            // Initialise OpenSSL & libsrtp, saves a couple of seconds for the first client connection.
            Console.WriteLine("Initialising OpenSSL and libsrtp...");
            DtlsHandshake.InitialiseOpenSSL();
            Srtp.InitialiseLibSrtp();

            Task.Run(DoDtlsHandshakeLoopbackTest).Wait();

            Console.WriteLine("Test DTLS handshake complete.");

            _mediaSource = new MediaSource();
            _mediaSource.Init(MP4_FILE_PATH, true);
            //_mediaSource.Init(0, 0, VideoSubTypesEnum.I420, 640, 480);

            // Start web socket.
            Console.WriteLine("Starting web socket server...");
            _webSocketServer = new WebSocketServer(IPAddress.Any, WEBSOCKET_PORT, true);
            _webSocketServer.SslConfiguration.ServerCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(WEBSOCKET_CERTIFICATE_PATH);
            _webSocketServer.SslConfiguration.CheckCertificateRevocation = false;
            //_webSocketServer.Log.Level = WebSocketSharp.LogLevel.Debug;
            _webSocketServer.AddWebSocketService<SDPExchange>("/", (sdpExchanger) =>
            {
                sdpExchanger.WebSocketOpened += SendSDPOffer;
                sdpExchanger.OnMessageReceived += WebSocketMessageReceived;
            });
            _webSocketServer.Start();

            Console.WriteLine($"Waiting for browser web socket connection to {_webSocketServer.Address}:{_webSocketServer.Port}...");

            // Ctrl-c will gracefully exit the call at any point.
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                exitMre.Set();
            };

            // Wait for a signal saying the call failed, was cancelled with ctrl-c or completed.
            exitMre.WaitOne();

            _mediaSource.Shutdown();
            _webSocketServer.Stop();
        }

        private static async Task<RTCPeerConnection> SendSDPOffer(WebSocketContext context)
        {
            logger.LogDebug($"Web socket client connection from {context.UserEndPoint}.");

            RTCConfiguration pcConfiguration = new RTCConfiguration
            {
                certificates = new List<RTCCertificate>
                {
                    new RTCCertificate
                    {
                        X_CertificatePath = DTLS_CERTIFICATE_PATH,
                        X_KeyPath = DTLS_KEY_PATH,
                        X_Fingerprint = DTLS_CERTIFICATE_FINGERPRINT
                    }
                }
            };

            var peerConnection = new RTCPeerConnection(pcConfiguration);
            var dtls = new DtlsHandshake(DTLS_CERTIFICATE_PATH, DTLS_KEY_PATH);

            MediaStreamTrack audioTrack = new MediaStreamTrack(SDPMediaTypesEnum.audio, false, new List<SDPMediaFormat> { new SDPMediaFormat(SDPMediaFormatsEnum.PCMU) });
            peerConnection.addTrack(audioTrack);
            MediaStreamTrack videoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, new List<SDPMediaFormat> { new SDPMediaFormat(SDPMediaFormatsEnum.VP8) });
            peerConnection.addTrack(videoTrack);

            peerConnection.OnReceiveReport += RtpSession_OnReceiveReport;
            peerConnection.OnSendReport += RtpSession_OnSendReport;

            peerConnection.OnTimeout += (mediaType) =>
            {
                peerConnection.Close("remote timeout");
                dtls.Shutdown();
            };

            peerConnection.onconnectionstatechange += (state) =>
            {
                if (state == RTCPeerConnectionState.closed || state == RTCPeerConnectionState.disconnected || state == RTCPeerConnectionState.failed)
                {
                    logger.LogDebug($"RTC peer connection was closed.");
                    OnMediaSampleReady -= peerConnection.SendMedia;
                    peerConnection.OnReceiveReport -= RtpSession_OnReceiveReport;
                    peerConnection.OnSendReport -= RtpSession_OnSendReport;
                }
                else if (state == RTCPeerConnectionState.connected)
                {
                    logger.LogDebug("Peer connection connected.");
                    OnMediaSampleReady += peerConnection.SendMedia;
                }
            };

            peerConnection.oniceconnectionstatechange += (state) =>
            {
                if (state == RTCIceConnectionState.connected)
                {
                    logger.LogDebug("Starting DTLS handshake task.");

                    bool dtlsResult = false;
                    Task.Run(async () => dtlsResult = await DoDtlsHandshake(peerConnection, dtls, peerConnection.RemotePeerDtlsFingerprint))
                    .ContinueWith((t) =>
                    {
                        logger.LogDebug($"dtls handshake result {dtlsResult}.");

                        if (dtlsResult)
                        {
                            //peerConnection.SetDestination(SDPMediaTypesEnum.audio, peerConnection.IceSession.ConnectedRemoteEndPoint, peerConnection.IceSession.ConnectedRemoteEndPoint);
                        }
                        else
                        {
                            dtls.Shutdown();
                            peerConnection.Close("dtls handshake failed.");
                        }
                    });
                }
            };

            var offerInit = peerConnection.createOffer(null);
            await peerConnection.setLocalDescription(offerInit);

            logger.LogDebug($"Sending SDP offer to client {context.UserEndPoint}.");

            context.WebSocket.Send(offerInit.sdp);

            return peerConnection;
        }

        private static async Task WebSocketMessageReceived(RTCPeerConnection peerConnection, string message)
        {
            try
            {
                if (peerConnection.remoteDescription == null)
                {
                    logger.LogDebug("Answer SDP: " + message);
                    peerConnection.setRemoteDescription(new RTCSessionDescriptionInit { sdp = message, type = RTCSdpType.answer });
                }
                else
                {
                    logger.LogDebug("ICE Candidate: " + message);
                    peerConnection.addIceCandidate(new RTCIceCandidateInit { candidate = message });
                }
            }
            catch (Exception excp)
            {
                logger.LogError("Exception WebSocketMessageReceived. " + excp.Message);
            }
        }

        /// <summary>
        /// Hands the socket handle to the DTLS context and waits for the handshake to complete.
        /// </summary>
        /// <param name="webRtcSession">The WebRTC session to perform the DTLS handshake on.</param>
        /// <returns>True if the handshake completed successfully or false otherwise.</returns>
        private static async Task<bool> DoDtlsHandshake(RTCPeerConnection peerConnection, DtlsHandshake dtls, byte[] sdpFingerprint)
        {
            logger.LogDebug("DoDtlsHandshake started.");

            byte[] clientFingerprint = null;
            int res = dtls.DoHandshakeAsServer((ulong)peerConnection.GetRtpChannel(SDPMediaTypesEnum.audio).RtpSocket.Handle, ref clientFingerprint);

            logger.LogDebug("DtlsContext initialisation result=" + res);

            if (dtls.IsHandshakeComplete())
            {
                // TODO: The client fingerprint should be checked against the SDP fingerprint.

                logger.LogDebug("DTLS negotiation complete.");

                var srtpSendContext = new Srtp(dtls, false);
                var srtpReceiveContext = new Srtp(dtls, true);

                peerConnection.SetSecurityContext(
                    srtpSendContext.ProtectRTP,
                    srtpReceiveContext.UnprotectRTP,
                    srtpSendContext.ProtectRTCP,
                    srtpReceiveContext.UnprotectRTCP);

                await peerConnection.Start();

                if (!_isSampling)
                {
                    _ = Task.Run(StartMedia);
                }

                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Video resolution changed event handler.
        /// </summary>
        /// <param name="width">The new video frame width.</param>
        /// <param name="height">The new video frame height.</param>
        /// <param name="stride">The new video frame stride.</param>
        private static void OnVideoResolutionChanged(uint width, uint height, uint stride)
        {
            try
            {
                if (_vpxEncoder == null ||
                    (_vpxEncoder.GetWidth() != width || _vpxEncoder.GetHeight() != height || _vpxEncoder.GetStride() != stride))
                {
                    if (_vpxEncoder != null)
                    {
                        _vpxEncoder.Dispose();
                    }

                    _vpxEncoder = new VpxEncoder();
                    _vpxEncoder.InitEncoder(width, height, stride);

                    logger.LogInformation($"VPX encoder initialised with width {width}, height {height} and stride {stride}.");
                }
            }
            catch (Exception excp)
            {
                logger.LogWarning("Exception OnVideoResolutionChangedEvent. " + excp.Message);
            }
        }

        /// <summary>
        /// Starts the Media Foundation sampling.
        /// </summary>
        unsafe private static void StartMedia()
        {
            try
            {
                logger.LogDebug("Starting media sampling thread.");

                _isSampling = true;

                while (true)
                {
                    if (OnMediaSampleReady == null)
                    {
                        logger.LogDebug("No active clients, media sampling paused.");
                        break;
                    }
                    else
                    {
                        byte[] sampleBuffer = null;
                        var sample = _mediaSource.GetSample(ref sampleBuffer);

                        if (sample != null && sample.HasVideoSample)
                        {
                            if (_vpxEncoder == null ||
                                (_vpxEncoder.GetWidth() != sample.Width || _vpxEncoder.GetHeight() != sample.Height || _vpxEncoder.GetStride() != sample.Stride))
                            {
                                OnVideoResolutionChanged((uint)sample.Width, (uint)sample.Height, (uint)sample.Stride);
                            }

                            byte[] vpxEncodedBuffer = null;

                            unsafe
                            {
                                fixed (byte* p = sampleBuffer)
                                {
                                    int encodeResult = _vpxEncoder.Encode(p, sampleBuffer.Length, 1, ref vpxEncodedBuffer);

                                    if (encodeResult != 0)
                                    {
                                        logger.LogWarning("VPX encode of video sample failed.");
                                    }
                                }
                            }

                            OnMediaSampleReady?.Invoke(SDPMediaTypesEnum.video, _vp8Timestamp, vpxEncodedBuffer);

                            //Console.WriteLine($"Video SeqNum {videoSeqNum}, timestamp {videoTimestamp}, buffer length {vpxEncodedBuffer.Length}, frame count {sampleProps.FrameCount}.");

                            _vp8Timestamp += VP8_TIMESTAMP_SPACING;

                        }
                        else if (sample != null && sample.HasAudioSample)
                        {
                            uint sampleDuration = (uint)(sampleBuffer.Length / 2);

                            byte[] mulawSample = new byte[sampleDuration];
                            int sampleIndex = 0;

                            for (int index = 0; index < sampleBuffer.Length; index += 2)
                            {
                                var ulawByte = MuLawEncoder.LinearToMuLawSample(BitConverter.ToInt16(sampleBuffer, index));
                                mulawSample[sampleIndex++] = ulawByte;
                            }

                            OnMediaSampleReady?.Invoke(SDPMediaTypesEnum.audio, _mulawTimestamp, mulawSample);

                            //Console.WriteLine($"Audio SeqNum {audioSeqNum}, timestamp {audioTimestamp}, buffer length {mulawSample.Length}.");

                            _mulawTimestamp += sampleDuration;
                        }
                    }
                }
            }
            catch (Exception excp)
            {
                logger.LogWarning("Exception OnProcessSampleEvent. " + excp.Message);
            }
            finally
            {
                logger.LogDebug("Media sampling thread stopped.");

                _isSampling = false;
            }
        }

        /// <summary>
        /// Diagnostic handler to print out our RTCP sender/receiver reports.
        /// </summary>
        private static void RtpSession_OnSendReport(SDPMediaTypesEnum mediaType, RTCPCompoundPacket sentRtcpReport)
        {
            if (sentRtcpReport.SenderReport != null)
            {
                var sr = sentRtcpReport.SenderReport;
                Console.WriteLine($"RTCP sent SR {mediaType}, ssrc {sr.SSRC}, pkts {sr.PacketCount}, bytes {sr.OctetCount}.");
            }
            else
            {
                var rrSample = sentRtcpReport.ReceiverReport.ReceptionReports.First();
                Console.WriteLine($"RTCP sent RR {mediaType}, ssrc {rrSample.SSRC}, seqnum {rrSample.ExtendedHighestSequenceNumber}.");
            }
        }

        /// <summary>
        /// Diagnostic handler to print out our RTCP reports from the remote WebRTC peer.
        /// </summary>
        private static void RtpSession_OnReceiveReport(SDPMediaTypesEnum mediaType, RTCPCompoundPacket recvRtcpReport)
        {
            var rr = recvRtcpReport.ReceiverReport.ReceptionReports.FirstOrDefault();
            if (rr != null)
            {
                logger.LogDebug($"RTCP {mediaType} Receiver Report: SSRC {rr.SSRC}, pkts lost {rr.PacketsLost}, delay since SR {rr.DelaySinceLastSenderReport}.");
            }
            else
            {
                logger.LogDebug($"RTCP {mediaType} Receiver Report: empty.");
            }
        }

        /// <summary>
        /// Runs a DTLS handshake test between two threads on a loopback address. The main motivation for
        /// this test was that the first DTLS handshake between this application and a client browser
        /// was often substantially slower and occasionally failed. By doing a loopback test the idea 
        /// is that the internal OpenSSL state is initialised.
        /// </summary>
        private static void DoDtlsHandshakeLoopbackTest()
        {
            IPAddress testAddr = IPAddress.Loopback;

            //var dtlsFingerprint = DTLS_CERTIFICATE_FINGERPRINT.Substring(DTLS_CERTIFICATE_FINGERPRINT.Length + 1).Trim().Replace(":", "").Replace(" ", "");
            //var dtlsFingerprintBuffer = SIPSorcery.Sys.ByteBufferInfo.ParseHexStr(dtlsFingerprint);

            Socket svrSock = new Socket(testAddr.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            svrSock.Bind(new IPEndPoint(testAddr, 9000));
            int svrPort = ((IPEndPoint)svrSock.LocalEndPoint).Port;
            DtlsHandshake svrHandshake = new DtlsHandshake(DTLS_CERTIFICATE_PATH, DTLS_KEY_PATH);
            svrHandshake.Debug = true;
            byte[] clientFingerprint = null;
            var svrTask = Task.Run(() => svrHandshake.DoHandshakeAsServer((ulong)svrSock.Handle, ref clientFingerprint));

            Socket cliSock = new Socket(testAddr.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            cliSock.Bind(new IPEndPoint(testAddr, 0));
            cliSock.Connect(testAddr, svrPort);
            DtlsHandshake cliHandshake = new DtlsHandshake();
            cliHandshake.Debug = true;
            byte[] serverFingerprint = null;
            var cliTask = Task.Run(() => cliHandshake.DoHandshakeAsClient((ulong)cliSock.Handle, (short)testAddr.AddressFamily, testAddr.GetAddressBytes(), (ushort)svrPort, ref serverFingerprint));

            bool result = Task.WaitAll(new Task[] { svrTask, cliTask }, TEST_DTLS_HANDSHAKE_TIMEOUT);

            cliHandshake.Shutdown();
            svrHandshake.Shutdown();
            cliSock.Close();
            svrSock.Close();
        }

        /// <summary>
        ///  Adds a console logger. Can be omitted if internal SIPSorcery debug and warning messages are not required.
        /// </summary>
        private static void AddConsoleLogger()
        {
            var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory();
            var loggerConfig = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Is(Serilog.Events.LogEventLevel.Debug)
                .WriteTo.Console()
                .CreateLogger();
            loggerFactory.AddSerilog(loggerConfig);
            SIPSorcery.Sys.Log.LoggerFactory = loggerFactory;
        }
    }
}
