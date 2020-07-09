// This Source Code Form is subject to the terms of the
// Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
// with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright 2020 Artem Yamshanov, me [at] anticode.ninja

﻿namespace RtpCast
{
    using System;
    using System.Net;

    using AntiFramework.Audio;
    using AntiFramework.Bindings.Opus;
    using AntiFramework.Network.Contracts;
    using AntiFramework.Network.Packets;
    using AntiFramework.Network.Transport;
    using AntiFramework.Utils;

    using NAudio.Wave;

    class Program
    {
        #region Constants

        private const int SAMPLE_RATE = 8000;

        private const int CHANNEL_COUNT = 1;

        private const int BIT_PER_SAMPLE = 16;

        private const int MS_IN_SECOND = 1000;

        private const byte PAYLOAD_TYPE_ULAW = 0;

        private const byte PAYLOAD_TYPE_ALAW = 8;

        private const byte PAYLOAD_TYPE_OPUS = 101;

        #endregion Constants

        #region Classes

        public class RtpContract : IPacketContract<IPacket>
        {
            public ParseResult TryParse(byte[] buffer, ref int offset, int end, out IPacket packet)
            {
                var result = RtpPacket.TryParse(buffer, ref offset, end, out var temp);
                packet = temp;
                return result;
            }

            public void Pack(ref byte[] buffer, ref int offset, IPacket packet)
            {
                packet.Pack(ref buffer, ref offset);
            }
        }

        public class JitterBufferAdapter : IWaveProvider
        {
            #region Fields

            private readonly JitterBuffer _jitterBuffer;

            #endregion Fields

            #region Properties

            public WaveFormat WaveFormat { get; }

            #endregion Properties

            #region Constructors

            public JitterBufferAdapter(int playBufferSize)
            {
                WaveFormat = new WaveFormat(SAMPLE_RATE, BIT_PER_SAMPLE, CHANNEL_COUNT);
                _jitterBuffer = new JitterBuffer(CodecFactory, playBufferSize);
            }

            #endregion Constructors

            #region Methods

            public void Add(RtpPacket packet) => _jitterBuffer.Write(packet);

            public int Read(byte[] buffer, int offset, int count) => _jitterBuffer.Read(buffer, offset, count);

            #endregion Methods
        }

        public class Opus : ICodec
        {
            private readonly OpusDecoder _decoder;
            private readonly OpusEncoder _encoder;

            public Opus()
            {
                _decoder = OpusDecoder.Create(SAMPLE_RATE, CHANNEL_COUNT);
                _encoder = OpusEncoder.Create(SAMPLE_RATE, CHANNEL_COUNT, OpusPInvoke.Application.Voip);
                _encoder.InbandFec = true;
                _encoder.LossPercentage = 30;
            }

            public int CalcSamplesNumber(byte[] source, int sourceOffset, int sourceLength)
                => _decoder.GetSamplesNumber(source, sourceOffset, sourceLength);

            public int Restore(byte[] source, int sourceOffset, int sourceLength, short[] target, int targetOffset, int targetLength)
                => _decoder.Decode(source, sourceOffset, sourceLength, target, targetOffset, targetLength, source != null);

            public int Decode(byte[] source, int sourceOffset, int sourceLength, short[] target, int targetOffset, int targetLength)
                => _decoder.Decode(source, sourceOffset, sourceLength, target, targetOffset, targetLength, false);

            public int Encode(short[] source, int sourceOffset, int sourceLength, byte[] target, int targetOffset, int length)
                => _encoder.Encode(source, sourceOffset, sourceLength, target, targetOffset, length);

            public void Dispose()
            {
                _decoder.Dispose();
                _encoder.Dispose();
            }
        }

        #endregion Classes

        #region Methods

        static void Main(string[] args)
        {
            var result = new ArgsParser(args)
                .Comment("small sample showing how to use \"jitter-adapter\" with NAudio")
                .Help("?", "help")
                .Keys("list").Tip("show list of audio devices").Subparser(ListMode)
                .Keys("sender").Tip("send audio from local device to remote").Subparser(SenderMode)
                .Keys("receiver").Tip("receive audio from remote and play locally").Subparser(ReceiverMode)
                .Result();

            if (result != null)
                Console.WriteLine(result);
        }

        public static ICodec CodecFactory(byte pt)
        {
            switch (pt)
            {
                case PAYLOAD_TYPE_ULAW: return new G711U();
                case PAYLOAD_TYPE_ALAW: return new G711A();
                case PAYLOAD_TYPE_OPUS: return new Opus();
                default: return null;
            }
        }

        private static void ListMode(ArgsParser parser)
        {
            if (parser
                .Result() != null)
                return;

            Console.WriteLine("Output:");
            for (var i = 0; i < WaveOut.DeviceCount; i++)
                Console.WriteLine("   {0}: {1}", i, WaveOut.GetCapabilities(i).ProductName);

            Console.WriteLine("Input:");
            for (var i = 0; i < WaveIn.DeviceCount; i++)
                Console.WriteLine("   {0}: {1}", i, WaveIn.GetCapabilities(i).ProductName);
        }

        private static void SenderMode(ArgsParser parser)
        {
            if (parser
                    .Keys("r", "remote").Value(out var remote, new IPEndPoint(IPAddress.Loopback, 19000))
                    .Keys("ssrc").Value(out var ssrc, 0x0000001u)
                    .Keys("d", "packet-duration").Value(out var packetDuration, 60u)
                    .Keys("i", "input-device").Value(out var device, 0)
                    .Result() != null)
                return;

            var rnd = new Random();
            var seqNumber = (ushort)rnd.Next();
            var timestamp = (uint)rnd.Next();
            var samples = new short[SAMPLE_RATE / MS_IN_SECOND * packetDuration];
            var marker = true;

            var codec = new Opus();
            var rtpSender = new UdpTransport<IPacket>(new RtpContract(), 0);
            rtpSender.Start();

            var waveIn = new WaveInEvent
            {
                DeviceNumber = device,
                WaveFormat = new WaveFormat(SAMPLE_RATE, BIT_PER_SAMPLE, CHANNEL_COUNT),
                BufferMilliseconds = (int) packetDuration,
            };

            waveIn.DataAvailable += (sender, e) =>
            {
                var payload = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
                Array.Resize(ref payload, codec.Encode(samples, 0, samples.Length, payload, 0, payload.Length));

                var packet = new RtpPacket
                    {
                        Ssrc = ssrc,
                        PayloadType = PAYLOAD_TYPE_OPUS,
                        Marker = marker,
                        Timestamp = timestamp,
                        SequenceNumber = seqNumber,
                        Payload = payload,
                    };

                rtpSender.Send(new PacketContainer<IPacket>
                    {
                        Target = remote,
                        Payload = packet
                    });

                timestamp += packetDuration;
                seqNumber += 1;
                marker = false;
            };
            waveIn.StartRecording();

            Console.WriteLine("Press any key to stop");
            Console.ReadKey();
        }

        private static void ReceiverMode(ArgsParser parser)
        {
            if (parser
                    .Keys("p", "port").Value<ushort>(out var port, 19000)
                    .Keys("s", "jitter-size").Value(out var jitterSize, 120)
                    .Keys("desired-latency").Value(out var desiredLatency, 60)
                    .Keys("o", "output-device").Value(out var device, 0)
                    .Result() != null)
                return;

            var jitterBufferAdapter = new JitterBufferAdapter(SAMPLE_RATE / MS_IN_SECOND * jitterSize);

            var rtpReceiver = new UdpTransport<IPacket>(new RtpContract(), port);
            rtpReceiver.Start();

            rtpReceiver.ReceivePacket += (sender, e) =>
            {
                if (e.Payload is RtpPacket packet)
                    jitterBufferAdapter.Add(packet);
            };

            var waveOut = new WaveOutEvent
            {
                DeviceNumber = device,
                DesiredLatency = desiredLatency,
            };
            waveOut.Init(jitterBufferAdapter);
            waveOut.Play();

            Console.WriteLine("Press any key to stop");
            Console.ReadKey();
        }

        #endregion Methods
    }
}
