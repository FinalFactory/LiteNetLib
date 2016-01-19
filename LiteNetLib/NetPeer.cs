using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace LiteNetLib
{
    public interface IPeerListener
    {
        void ReceiveFromPeer(NetPacket packet, NetEndPoint endPoint);
        void ProcessSendError(NetEndPoint endPoint);
    }

    public class NetPeer
    {
        private enum FlowMode
        {
            Bad,
            Good
        }

        //Flow control
        private FlowMode _currentFlowMode;
        private int _sendedPacketsCount;
        private int _flowTimer;

        private readonly NetSocket _socket;              //Udp socket
        private readonly Queue<NetPacket> _outgoingQueue;//Queue for sending packets
        private readonly Stack<NetPacket> _packetPool;   //Pool for packets

        private int _rtt;                                //round trip time
        private int _avgRtt;
        private int _rttCount;
        private int _badRoundTripTime;
        private int _goodRttCount;
        private ushort _pingSequence;
        private ushort _remotePingSequence;
        private ushort _pongSequence;

        private int _rttUpdateDelay;
        private int _rttUpdateTimer;
        private const int RttResetDelay = 1000;
        private int _rttResetTimer;

        private const int FlowUpdateTime = 100;
        private const int ThrottleIncreaseThreshold = 32;

        private readonly int[] _flowModes;
        private readonly Stopwatch _pingStopwatch;
        private readonly NetEndPoint _remoteEndPoint;
        private readonly ReliableOrderedChannel _reliableOrderedChannel;
        private readonly ReliableUnorderedChannel _reliableUnorderedChannel;
        private readonly SequencedChannel _sequencedChannel;
        private readonly long _id;
        private readonly IPeerListener _peerListener;

        private readonly object _sendLock = new object();

        //DEBUG
        public ConsoleColor DebugTextColor = ConsoleColor.DarkGreen;

        public NetEndPoint EndPoint
        {
            get { return _remoteEndPoint; }
        }

        public int BadRoundTripTime
        {
            get { return _badRoundTripTime; }
            set { _badRoundTripTime = value; }
        }

        public int Ping
        {
            get { return _avgRtt; }
        }

        public long Id
        {
            get { return _id; }
        }

        public NetPeer(IPeerListener peerListener, NetSocket socket, NetEndPoint remoteEndPoint)
        {
            _id = NetUtils.GetIdFromEndPoint(remoteEndPoint);
            _peerListener = peerListener;
            
            _socket = socket;
            _remoteEndPoint = remoteEndPoint;

            _outgoingQueue = new Queue<NetPacket>();
            _flowModes = new int[2];
            _flowModes[0] = 64 / 4; //bad
            _flowModes[1] = 64;     //good

            _avgRtt = 0;
            _rtt = 0;
            _badRoundTripTime = 650;
            _rttUpdateDelay = 1000;
            _rttUpdateTimer = 0;

            _pingStopwatch = new Stopwatch();

            _reliableOrderedChannel = new ReliableOrderedChannel(this);
            _reliableUnorderedChannel = new ReliableUnorderedChannel(this);
            _sequencedChannel = new SequencedChannel(this);

            _packetPool = new Stack<NetPacket>();
        }

        public void Send(byte[] data, SendOptions options)
        {
            NetPacket packet = CreatePacket();
            packet.Data = data;

            switch (options)
            {
                case SendOptions.Reliable:
                    packet.Property = PacketProperty.Reliable;
                    break;
                case SendOptions.Sequenced:
                    packet.Property = PacketProperty.Sequenced;
                    break;
                case SendOptions.ReliableOrdered:
                    packet.Property = PacketProperty.ReliableOrdered;
                    break;
                default:
                    packet.Property = PacketProperty.None;
                    break;
            }

            SendPacket(packet);
        }

        public void Send(byte[] data, PacketProperty property)
        {
            NetPacket packet = CreatePacket(property);
            packet.Property = property;
            packet.Data = data;
            SendPacket(packet);
        }

        public void Send(PacketProperty property)
        {
            NetPacket packet = CreatePacket(property);
            packet.Property = property;
            SendPacket(packet);
        }

        public void SendPacket(NetPacket packet)
        {
            lock (_sendLock)
            {
                switch (packet.Property)
                {
                    case PacketProperty.Reliable:
                        //DebugWrite("[RS]Packet reliable");
                        _reliableUnorderedChannel.AddToQueue(packet);
                        break;
                    case PacketProperty.Sequenced:
                        //DebugWrite("[RS]Packet sequenced");
                        _sequencedChannel.AddToQueue(packet);
                        break;
                    case PacketProperty.ReliableOrdered:
                        //DebugWrite("[RS]Packet reliable ordered");
                        _reliableOrderedChannel.AddToQueue(packet);
                        break;
                    case PacketProperty.AckReliable:
                    case PacketProperty.AckReliableOrdered:
                    case PacketProperty.Connect:
                    case PacketProperty.Disconnect:
                    case PacketProperty.None:
                        DebugWrite("[RS]Packet simple");
                        _outgoingQueue.Enqueue(packet);
                        break;
                    case PacketProperty.Ping:
                    case PacketProperty.Pong:
                        _socket.SendTo(packet, _remoteEndPoint);
                        break;
                    default:
                        throw new Exception("Unknown packet property: " + packet.Property);
                }
            }
        }

        public void UpdateRoundTripTime(int roundTripTime)
        {
            _rtt += roundTripTime;
            _rttCount++;
            _avgRtt = _rtt/_rttCount;

            if (_avgRtt < _badRoundTripTime)
            {
                _goodRttCount++;
                if (_goodRttCount > ThrottleIncreaseThreshold && _currentFlowMode != FlowMode.Good)
                {
                    _goodRttCount = 0;
                    DebugWrite("[PA]Enabled good flow mode, RTT: {0}", _avgRtt);
                    _currentFlowMode = FlowMode.Good;
                }
            }
            else
            {
                _goodRttCount = 0;
                if (_currentFlowMode != FlowMode.Bad)
                {
                    DebugWrite("[PA]Enabled bad flow mode, RTT: {0}", _avgRtt);
                    _currentFlowMode = FlowMode.Bad;
                }
            }
        }

        public void DebugWrite(string str, params object[] args)
        {
            NetUtils.DebugWrite(DebugTextColor, str, args);
        }

        public void DebugWriteForce(string str, params object[] args)
        {
            NetUtils.DebugWrite(true, DebugTextColor, str, args);
        }

        public NetPacket CreatePacket(PacketProperty property = PacketProperty.None)
        {
            lock (_packetPool)
            {
                var packet = _packetPool.Count > 0
                   ? _packetPool.Pop()
                   : new NetPacket();

                packet.Property = property;
                return packet;
            }
        }

        public void Recycle(NetPacket packet)
        {
            packet.Data = null;
            lock (_packetPool)
            {
                _packetPool.Push(packet);
            }
        }

        public void AddIncomingPacket(NetPacket packet)
        {
            _peerListener.ReceiveFromPeer(packet, _remoteEndPoint);
            Recycle(packet);
        }

        //Process incoming packet
        public void ProcessPacket(NetPacket packet)
        {
            DebugWrite("[RR]PacketProperty: {0}", packet.Property);
            switch (packet.Property)
            {
                //If we get ping, send pong
                case PacketProperty.Ping:
                    if (NetUtils.RelativeSequenceNumber(packet.Sequence, _remotePingSequence) < 0)
                    {
                        break;
                    }
                    _remotePingSequence = packet.Sequence;
                    NetPacket pongPacket = CreatePacket(PacketProperty.Pong);
                    pongPacket.Sequence = packet.Sequence;
                    SendPacket(pongPacket);
                    break;

                //If we get pong, calculate ping time and rtt
                case PacketProperty.Pong:
                    if (NetUtils.RelativeSequenceNumber(packet.Sequence, _pongSequence) < 0)
                    {
                        break;
                    }
                    _pongSequence = packet.Sequence;
                    int rtt = (int) _pingStopwatch.ElapsedMilliseconds;
                    _pingStopwatch.Reset();
                    UpdateRoundTripTime(rtt);
                    DebugWrite("[PP]Ping: {0}", rtt);
                    break;

                //Process ack
                case PacketProperty.AckReliable:
                    _reliableUnorderedChannel.ProcessAck(packet.Data);
                    break;

                case PacketProperty.AckReliableOrdered:
                    _reliableOrderedChannel.ProcessAck(packet.Data);
                    break;

                //Process in order packets
                case PacketProperty.Sequenced:
                    if (_sequencedChannel.ProcessPacket(packet))
                    {
                        //do not recycle
                        return;
                    }
                    break;

                case PacketProperty.Reliable:
                    if (_reliableUnorderedChannel.ProcessPacket(packet))
                    {
                        //do not recycle
                        return;
                    }
                    break;

                case PacketProperty.ReliableOrdered:
                    if (_reliableOrderedChannel.ProcessPacket(packet))
                    {
                        //do not recycle
                        return;
                    }
                    break;

                //Simple packet without acks
                case PacketProperty.None:
                case PacketProperty.Connect:
                case PacketProperty.Disconnect:
                    _peerListener.ReceiveFromPeer(packet, _remoteEndPoint);
                    return; //do not recycle
            }

            Recycle(packet);
        }

        public void Update(int deltaTime)
        {
            int currentSended = 0;
            //Get current flow mode
            int maxSendPacketsCount = _flowModes[(int) _currentFlowMode];
            int availableSendPacketsCount = maxSendPacketsCount - _sendedPacketsCount;
            int currentMaxSend = Math.Min(availableSendPacketsCount, (maxSendPacketsCount*deltaTime)/ FlowUpdateTime);

            DebugWrite("[UPDATE]Delta: {0}ms, MaxSend: {1}", deltaTime, currentMaxSend);

            //Pending send
            while (currentSended < currentMaxSend)
            {
                //Get one of packets
                NetPacket packet = _reliableOrderedChannel.GetQueuedPacket();
                if (packet == null)
                    packet = _reliableUnorderedChannel.GetQueuedPacket();
                if (packet == null)
                    packet = _sequencedChannel.GetQueuedPacket();
                if (packet == null)
                {
                    if (_outgoingQueue.Count > 0)
                        packet = _outgoingQueue.Dequeue();
                    else
                        break;
                }
                    
                if (_socket.SendTo(packet, _remoteEndPoint) == -1)
                {
                    _peerListener.ProcessSendError(_remoteEndPoint);
                    return;
                }
                currentSended++;
            }

            //Increase counter
            _sendedPacketsCount += currentSended;

            //ResetFlowTimer
            _flowTimer += deltaTime;
            if (_flowTimer >= FlowUpdateTime)
            {
                DebugWrite("[UPDATE]Reset flow timer, _sendedPackets - {0}", _sendedPacketsCount);
                _sendedPacketsCount = 0;
                _flowTimer = 0;
            }

            //Send ping
            _rttUpdateTimer += deltaTime;
            if (_rttUpdateTimer >= _rttUpdateDelay)
            {
                //reset timer
                _rttUpdateTimer = 0;

                //create packet
                NetPacket packet = CreatePacket(PacketProperty.Ping);
                packet.Sequence = _pingSequence;
                _pingSequence++;

                //send
                SendPacket(packet);
                Recycle(packet);

                //reset timer
                _pingStopwatch.Restart();
            }

            //reset rtt
            _rttResetTimer += deltaTime;
            if (_rttResetTimer >= RttResetDelay)
            {
                _rtt = 0;
                _rttCount = 0;
            }
        }
    }
}
