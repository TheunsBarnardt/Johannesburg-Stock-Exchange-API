using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace JSE
{
    public class TcpClient
    {
        #region Public Constants
        public const int KeepAlive = 1000;
        public const int BufferSize = 255 * 255;
        #endregion

        #region Public
        public bool EnableReconnect = true;
        public int ReconnectAttempts = 10;
        public int Port;
        public string Host;
        #endregion

        #region Events
        public event Connected_Handler OnConnected = delegate { };
        public event Disconnected_Handler OnDisconnected = delegate { };
        public event Error_Handler OnError = delegate { };
        public event InternalError_Handler OnInternalError = delegate { };
        public event DataIn_Handler OnDataIn = delegate { };
        public event Reconnect_Handler OnReconnect = delegate { };

        public delegate void Connected_Handler(TcpClient socket);
        public delegate void Disconnected_Handler(TcpClient socket);
        public delegate void Error_Handler(TcpClient socket, SocketException exception);
        public delegate void InternalError_Handler(TcpClient socket, Exception exception);
        public delegate void DataIn_Handler(TcpClient socket, byte[] data);
        public delegate void Reconnect_Handler(TcpClient socket);

        public void Connected(TcpClient socket) { OnConnected?.Invoke(socket); }
        public void Disconnected(TcpClient socket) { OnDisconnected?.Invoke(socket); }
        public void InternalError(TcpClient socket, Exception exception) { OnInternalError?.Invoke(socket, exception); }
        public void Error(TcpClient socket, SocketException exception) { OnError?.Invoke(socket, exception); }
        public void DataIn(TcpClient socket, byte[] data) { OnDataIn?.Invoke(socket, data); }
        public void Reconnect(TcpClient socket) { OnReconnect?.Invoke(socket); }
        #endregion

        #region Private
        private bool _IsReconnect = false;
        private bool _Running;
        private object _threadReceived = new object();
        private object _threadSend = new object();
        private int _ReconnectAttempt = 0;
        private byte[] _data = new byte[BufferSize];
        private System.Net.Sockets.Socket _Socket;
        private ConcurrentQueue<byte[]> _receiveQueue = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<byte[]> _sendeQueue = new ConcurrentQueue<byte[]>();
        #endregion

        #region Public Methods
        public void Connect()
        {
            Connect(Host, Port);
        }

        public void Connect(string Host, int Port)
        {
            _Socket = new System.Net.Sockets.Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                _Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                _Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true);
                _Socket.NoDelay = true;
                _Socket.ReceiveBufferSize = BufferSize;
                _Socket.SendBufferSize = BufferSize;
                _Socket.Blocking = false;
                this.Host = Host;
                this.Port = Port;
                IPEndPoint iep = new IPEndPoint(IPAddress.Parse(Host), Port);
                _Socket.BeginConnect(iep, new AsyncCallback(ConnectedHandeler), _Socket);
            }
            catch (SocketException ex)
            {
                Error(this, ex);
            }
            catch (Exception ex)
            {
                InternalError(this, ex);
            }
        }

        public void Disconnect()
        {
            try
            {
                Disconnected(this);
                _Running = false;
                lock (_threadReceived) { Monitor.Pulse(_threadReceived); }
                lock (_threadSend) { Monitor.Pulse(_threadSend); }
                byte[] item;
                while (_receiveQueue.TryDequeue(out item)) { }
                while (_sendeQueue.TryDequeue(out item)) { }
                _Socket.BeginDisconnect(true, new AsyncCallback(Disconnect), _Socket);
            }
            catch (SocketException ex)
            {
                Error(this, ex);
            }
            catch (Exception ex)
            {
                InternalError(this, ex);
            }
        }

        public void Send(byte[] value)
        {
            try
            {
                if (_Socket != null && _Running)
                {
                    if (_Socket.Connected)
                    {
                        _sendeQueue.Enqueue(value);
                        lock (_threadSend)
                        {
                            Monitor.Pulse(_threadSend);
                        }
                    }
                }
            }
            catch (SocketException ex)
            {
                Error(this, ex);
            }
            catch (Exception ex)
            {
                InternalError(this, ex);
            }
        }

        public bool IsConnected
        {
            get
            {
                try
                {
                    if (_Socket == null)
                        return false;

                    return _Socket.Connected;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public string RemoteEndPoint
        {
            get
            {
                try
                {
                    if (_Socket == null)
                        return "";

                    return _Socket.RemoteEndPoint.ToString();
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        public string LocalEndPoint
        {
            get
            {
                try
                {
                    if (_Socket == null)
                        return "";

                    return _Socket.LocalEndPoint.ToString();
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }


        public long ReceiveQueueSize
        {
            get
            {
                try
                {
                    if (_receiveQueue == null)
                        return 0;

                    return _receiveQueue.Count;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        public long SendeQueueSize
        {
            get
            {
                try
                {
                    if (_sendeQueue == null)
                        return 0;

                    return _sendeQueue.Count;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }
        #endregion

        #region Private Methods
        private void ConnectedHandeler(IAsyncResult iar)
        {
            _Socket = (System.Net.Sockets.Socket)iar.AsyncState;
            try
            {
                if (_Socket.Connected)
                {
                    _Socket.EndConnect(iar);
                    EnableReconnect = true;
                    _Running = true;
                    _ReconnectAttempt = 0;
                    if (_IsReconnect)
                    {
                        Reconnect(this);
                        _IsReconnect = false;
                    }
                    else
                        Connected(this);

                    SttThreader.StartNamedBackgroundThread($"Received Handler", ReceivedHandler);
                    SttThreader.StartNamedBackgroundThread($"Send Handler", SendHandler);
                    SttThreader.StartNamedBackgroundThread($"Client Polling", PollHandler);
                    _Socket.BeginReceive(_data, 0, _data.Length, SocketFlags.None, new AsyncCallback(ReceiveData), _Socket);
                }
                else
                {
                    Reconnect();
                }
            }
            catch (SocketException ex)
            {
                if (!EnableReconnect)
                {
                    Error(this, ex);
                }
                else
                {
                    Reconnect();
                }
            }
            catch (Exception ex)
            {
                InternalError(this, ex);
            }
        }

        private void ReceivedHandler()
        {
            while (_Running)
            {
                try
                {
                    while (_receiveQueue.IsEmpty && _Running)
                    {
                        lock (_threadReceived)
                        {
                            Monitor.Wait(_threadReceived);
                        }
                    }
                    byte[] packet;
                    if (_receiveQueue.TryDequeue(out packet))
                    {
                        if (packet != null)
                        {
                            DataIn(this, packet);
                        }
                    }
                }
                catch (SocketException ex)
                {
                    Error(this, ex);
                }
                catch (Exception ex)
                {
                    InternalError(this, ex);
                }
            }
        }


        private void SendHandler()
        {
            while (_Running)
            {
                try
                {
                    while (_sendeQueue.IsEmpty && _Running && IsConnected)
                    {
                        lock (_threadSend)
                        {
                            Monitor.Wait(_threadSend);
                        }
                    }
                    byte[] packet;
                    if (_sendeQueue.TryDequeue(out packet))
                    {
                        if (packet != null)
                        {
                            if (IsConnected)
                            {
                                _Socket.BeginSend(packet, 0, packet.Length, SocketFlags.None, new AsyncCallback(SendData), _Socket);
                            }
                        }
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode != SocketError.ConnectionReset)
                    {
                        Error(this, ex);
                    }
                }
                catch (Exception ex)
                {
                    InternalError(this, ex);
                }
            }
        }

        private void PollHandler()
        {
            while (_Running)
            {
                try
                {
                    if (_Socket != null)
                    {
                        if (!IsConnected)
                        {
                            if (_Socket.Poll(KeepAlive, SelectMode.SelectRead) && _Socket.Available == 0)
                            {
                                Reconnect();
                            }
                        }
                        Thread.Sleep(KeepAlive);
                    }
                }
                catch (SocketException ex)
                {
                    Error(this, ex);
                }
                catch (Exception ex)
                {
                    InternalError(this, ex);
                }
            }
        }

        private void Reconnect()
        {
            try
            {
                if (EnableReconnect)
                {
                    _IsReconnect = true;
                    _Running = false;
                    Thread.Sleep(KeepAlive);
                    _ReconnectAttempt++;

                    if (_ReconnectAttempt <= ReconnectAttempts)
                    {
                        Connect(this.Host, this.Port);
                    }
                    else
                    {
                        _Running = false;
                        EnableReconnect = false;
                        Disconnected(this);
                    }
                }
                else
                {
                    _Running = false;
                    Disconnected(this);
                }
            }
            catch (SocketException ex)
            {
                Error(this, ex);
            }
            catch (Exception ex)
            {
                InternalError(this, ex);
            }
        }

        private void SendData(IAsyncResult iar)
        {
            System.Net.Sockets.Socket remote = (System.Net.Sockets.Socket)iar.AsyncState;
            try
            {
                if (remote.Connected)
                {
                    try
                    {
                        int sent = remote.EndSend(iar);
                    }
                    catch (SocketException ex)
                    {
                        Error(this, ex);
                    }
                }
                else
                    Reconnect();
            }
            catch (SocketException ex)
            {
                Error(this, ex);
            }
            catch (Exception ex)
            {
                InternalError(this, ex);
            }
        }

        private void ReceiveData(IAsyncResult iar)
        {
            System.Net.Sockets.Socket remote = (System.Net.Sockets.Socket)iar.AsyncState;
            if (remote.Connected)
            {
                try
                {
                    int recv = remote.EndReceive(iar);
                    if (recv != 0)
                    {
                        byte[] temp = new byte[recv];
                        Array.Copy(_data, temp, recv);
                        _receiveQueue.Enqueue(temp);
                        lock (_threadReceived)
                        {
                            Monitor.Pulse(_threadReceived);
                        }
                        remote.BeginReceive(_data, 0, _data.Length, SocketFlags.None, new AsyncCallback(ReceiveData), remote);
                    }
                    else
                    {
                        Disconnect();
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode != SocketError.ConnectionReset)
                    {
                        Error(this, ex);
                    }
                }
                catch (Exception ex)
                {
                    InternalError(this, ex);
                }
            }
        }

        private void Disconnect(IAsyncResult iar)
        {
            _Socket = (System.Net.Sockets.Socket)iar.AsyncState;
            try
            {
                _Socket.EndDisconnect(iar);
                EnableReconnect = false;
                _Socket.Close();
            }
            catch (SocketException ex)
            {
                Error(this, ex);
            }
            catch (Exception ex)
            {
                InternalError(this, ex);
            }
        }
        #endregion
    }
}