﻿#define DEBUG
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace GServer
{
    public class Datagram
    {
        public readonly byte[] Buffer;
        public readonly IPEndPoint EndPoint;
        public Datagram(byte[] buffer, IPEndPoint ipEndpoint)
        {
            Buffer = buffer;
            EndPoint = ipEndpoint;
        }
    }

    public delegate void ReceiveHandler(Message msg, Connection con);
    public class Host
    {
        private Token _hostToken;
        private readonly Queue<Datagram> _datagrams;
        private UdpClient _client;
        private readonly Thread _listenThread;
        private readonly Thread _connectionCleaningThread;
        private readonly List<Thread> _processingThreads;
        private readonly ConnectionManager _connectionManager;
        private bool _isListening;
        private IDictionary<short, IList<ReceiveHandler>> _receiveHandlers;
        public event Action<IPEndPoint> Connected;
        public Host(int port)
        {
            _listenThread = new Thread(() => Listen(port));
            _datagrams = new Queue<Datagram>();
            _processingThreads = new List<Thread>();
            _isListening = false;
            _connectionManager = new ConnectionManager();
            _connectionCleaningThread = new Thread(CleanConnections);
            _receiveHandlers = new Dictionary<short, IList<ReceiveHandler>>();
            _connectionManager.HandshakeRecieved += SendToken;
        }
        private void SendToken(Connection con)
        {
            Message msg = new Message(MessageType.Token, Mode.None, null);
            msg.ConnectionToken = con.Token;
            Send(msg, con);
        }
        private void CleanConnections()
        {
            lock (_connectionManager)
            {
                _connectionManager.RemoveNotActive();
            }
        }
        private void Listen(int port)
        {
            _client.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            while (_isListening)
            {
                if (_client.Available > 0)
                {

                    IPEndPoint endPoint = null;
                    var buffer = _client.Receive(ref endPoint);
                    var datagram = new Datagram(buffer, endPoint);
                    if (_processingThreads.Count > 0)
                    {
                        lock (_datagrams)
                        {
                            _datagrams.Enqueue(datagram);
                        }
                    }
                    else
                    {
                        ProcessDatagram(datagram);
                    }

                }
            }
        }
        private void ProcessQueue()
        {
            while (_isListening)
            {
                Datagram prcessDgram = null;
                lock (_datagrams)
                {
                    if (_datagrams.Count != 0)
                    {
                        prcessDgram = _datagrams.Dequeue();
                    }
                }
                if (prcessDgram == null)
                    Thread.Sleep(1000);
                else
                    ProcessDatagram(prcessDgram);
            }
        }
        private void ProcessDatagram(Datagram datagram)
        {
            if (datagram.Buffer.Length == 0)
                return;
            var msg = Message.Deserialize(datagram.Buffer);
            if (Connected != null)
            {
                Connected.Invoke(datagram.EndPoint);
            }
            Connection connection;
            if (_connectionManager.TryGetConnection(out connection, msg, datagram.EndPoint))
            {
                if (msg.Header.Reliable)
                {
                    Send(connection.GenerateAck(msg), connection);
                }

                if (msg.Header.Sequenced)
                {
                    if (connection.IsMessageInItsOrder((short)msg.Header.Type, msg.Header.MessageId))
                    {
                        DatagramHandler(msg, connection);
                    }
                }
                else if (msg.Header.Ordered)
                {
                    var toInvoke = connection.MessagesToInvoke(msg);
                    if (toInvoke == null)
                    {
                        return;
                    }
                    DatagramHandler(toInvoke, connection);
                }
                else
                {
                    DatagramHandler(msg, connection);
                }
            }
        }
        private void DatagramHandler(Message msg, Connection connection)
        {
            IList<ReceiveHandler> handlers = null;
            lock (_receiveHandlers)
            {
                if (_receiveHandlers.ContainsKey((short)msg.Header.Type))
                {
                    handlers = _receiveHandlers[(short)msg.Header.Type];
                }
            }
            if (handlers != null)
            {
                connection = _connectionManager[msg.Header.ConnectionToken];
                foreach (var h in handlers)
                {
                    try
                    {
                        h.Invoke(msg, connection);

                    }
                    catch (Exception ex)
                    {
                        if (ErrLog != null)
                            ErrLog.Invoke(ex.Message);
                    }
                }
                connection.UpdateActivity();
            }
        }
        private void DatagramHandler(List<Message> messages, Connection connection)
        {
            IList<ReceiveHandler> handlers = null;
            if (messages.Count == 0)
            {
                return;
            }
            var msg = messages[0];
            lock (_receiveHandlers)
            {
                if (_receiveHandlers.ContainsKey((short)msg.Header.Type))
                {
                    handlers = _receiveHandlers[(short)msg.Header.Type];
                }
            }
            if (handlers != null)
            {
                connection = _connectionManager[msg.Header.ConnectionToken];
                foreach (var h in handlers)
                {
                    foreach (var m in messages)
                    {
                        try
                        {
                            h.Invoke(m, connection);
                        }
                        catch (Exception ex)
                        {
                            if (ErrLog != null)
                                ErrLog.Invoke(ex.Message);
                        }
                    }
                }
                connection.UpdateActivity();
            }
        }
        public void StartListen(int threadCount)
        {
            for (int i = 0; i < threadCount; i++)
            {
                var thread = new Thread(ProcessQueue);
                _processingThreads.Add(thread);
            }
            _isListening = true;
            foreach (var thread in _processingThreads)
            {
                thread.Start();
            }
            _client = new UdpClient();
            _client.ExclusiveAddressUse = false;
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, false);
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listenThread.Start();
            _connectionCleaningThread.Start();
        }
        public void StopListen()
        {
            _isListening = false;
            _processingThreads.Clear();
            _client.Close();
        }
        public void Send(Message msg, Connection con)
        {
            try
            {
                msg.ConnectionToken = con.Token;
                msg.MessageId = con.GetMessageId(msg);
                var buffer = msg.Serialize();
                _client.Send(buffer, buffer.Length, con.EndPoint);
            }
            catch (Exception ex)
            {
                ErrLog.Invoke(ex.Message);
            }
        }
        public void Send(Message msg)
        {
            try
            {
                Connection connection;
                lock (_connectionManager)
                {
                    connection = _connectionManager[_hostToken];
                }
                msg.MessageId = connection.GetMessageId(msg);
                msg.ConnectionToken = _hostToken;
                var buffer = msg.Serialize();
                _client.Send(buffer, buffer.Length);
            }
            catch (Exception ex)
            {
                ErrLog.Invoke(ex.Message);
            }
        }
        public void AddHandler(short type, ReceiveHandler handler)
        {
            lock (_receiveHandlers)
            {
                if (_receiveHandlers.ContainsKey(type))
                {
                    _receiveHandlers[type].Add(handler);
                }
                else
                {
                    IList<ReceiveHandler> list = new List<ReceiveHandler>();
                    list.Add(handler);
                    _receiveHandlers.Add(type, list);
                }
            }
        }
        public bool Connect(IPEndPoint ep)
        {
            try
            {
                _client.Connect(ep);
            }
            catch
            {
                return false;
            }
            var buffer = Message.Handshake.Serialize();
            _client.Send(buffer, buffer.Length);
            IPEndPoint remoteEp = null;
            while (true)
            {
                byte[] recieved = _client.Receive(ref remoteEp);
                if (remoteEp.Address.ToString() == ep.Address.ToString() && remoteEp.Port == ep.Port)
                {
                    var msg = Message.Deserialize(recieved);
                    _hostToken = msg.ConnectionToken;
                    Connection con = new Connection(ep, _hostToken);
                    lock (_connectionManager)
                    {
                        _connectionManager.Add(con.Token, con);
                    }
                    break;
                }
            }
            return true;
        }
        public Action<string> ErrLog;
        public Action<string> DebugLog;
    }
}
