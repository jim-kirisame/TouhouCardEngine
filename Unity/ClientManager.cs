﻿using System;
using System.Reflection;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using TouhouCardEngine.Interfaces;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Runtime.Serialization.Json;
using MongoDB.Bson.Serialization;
using MongoDB.Bson;
using System.Threading;
using System.Linq;
using System.Reflection.Emit;
namespace TouhouCardEngine
{
    public class ClientManager : MonoBehaviour, IClientManager, INetEventListener
    {
        [SerializeField]
        int _port = 9050;
        public int port
        {
            get { return _port; }
        }
        [SerializeField]
        float _timeout = 30;
        public float timeout
        {
            get { return _timeout; }
            set
            {
                _timeout = value;
                if (net != null)
                    net.DisconnectTimeout = (int)(value * 1000);
            }
        }
        [SerializeField]
        bool _autoStart = false;
        public bool autoStart
        {
            get { return _autoStart; }
            set { _autoStart = value; }
        }
        /// <summary>
        /// 
        /// </summary>
        /// <remarks>
        /// Host和Client都有NetManager是因为在同一台电脑上如果要开Host和Client进行网络对战的话，就必须得开两个端口进行通信，出于这样的理由
        /// Host和Client都必须拥有一个NetManager实例并使用不同的端口。
        /// </remarks>
        NetManager net { get; set; } = null;
        public bool isRunning
        {
            get { return net != null ? net.IsRunning : false; }
        }
        NetPeer host { get; set; } = null;
        public Interfaces.ILogger logger { get; set; } = null;
        [SerializeField]
        int _id = -1;
        public int id
        {
            get { return _id; }
            private set { _id = value; }
        }
        protected void Awake()
        {
            net = new NetManager(this)
            {
                AutoRecycle = true,
                UnconnectedMessagesEnabled = true,
                DisconnectTimeout = (int)(timeout * 1000),
                IPv6Enabled = false
            };
        }
        protected void Start()
        {
            if (autoStart)
            {
                if (port > 0)
                    start(port);
                else
                    start();
            }
        }
        protected void Update()
        {
            net.PollEvents();
        }
        public void start()
        {
            if (!net.IsRunning)
            {
                net.Start();
                _port = net.LocalPort;
                logger?.log("客户端初始化，本地端口：" + net.LocalPort);
            }
            else
                logger?.log("Warning", "客户端已经初始化，本地端口：" + net.LocalPort);
        }
        public void start(int port)
        {
            if (!net.IsRunning)
            {
                net.Start(port);
                _port = net.LocalPort;
                logger?.log("客户端初始化，本地端口：" + net.LocalPort);
            }
            else
                logger?.log("Warning", "客户端已经初始化，本地端口：" + net.LocalPort);
        }
        TaskCompletionSource<object> tcs { get; set; } = null;
        public async Task<int> join(string ip, int port)
        {
            if (tcs != null)
                throw new InvalidOperationException("客户端正在执行另一项操作");
            NetDataWriter writer = new NetDataWriter();
            host = net.Connect(new IPEndPoint(IPAddress.Parse(ip), port), writer);
            logger?.log("客户端正在连接主机" + ip + ":" + port);
            tcs = new TaskCompletionSource<object>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(net.DisconnectTimeout);
                if (tcs != null)
                    tcs.SetException(new TimeoutException("客户端连接主机" + ip + ":" + port + "超时"));
            });
            await tcs.Task;
            int result = tcs.Task.IsCompleted ? (int)tcs.Task.Result : -1;
            tcs = null;
            return result;
        }
        public void OnPeerConnected(NetPeer peer)
        {
            if (peer == host)
                logger?.log("客户端连接到主机" + peer.EndPoint);
        }
        public event Action onConnected;
        public void send(object obj)
        {
            send(obj, PacketType.sendRequest);
        }
        void send(object obj, PacketType packetType)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));
            if (tcs != null)
                throw new InvalidOperationException("客户端正在执行另一项操作");
            NetDataWriter writer = new NetDataWriter();
            writer.Put((int)packetType);
            writer.Put(id);
            writer.Put(obj.GetType().FullName);
            writer.Put(obj.ToJson());
            host.Send(writer, DeliveryMethod.ReliableOrdered);
        }
        public async Task<T> send<T>(T obj)
        {
            return await send<T>(obj, PacketType.sendRequest);
        }
        async Task<T> send<T>(T obj, PacketType packetType)
        {
            send(obj as object, packetType);
            tcs = new TaskCompletionSource<object>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(net.DisconnectTimeout);
                if (tcs != null)
                    tcs.SetException(new TimeoutException("客户端" + id + "向主机" + host.EndPoint + "发送数据响应超时：" + obj));
            });
            await tcs.Task;
            T result = tcs.Task.IsCompleted ? (T)tcs.Task.Result : default;
            tcs = null;
            return result;
        }
        public Task<T> invokeHost<T>(string method, params object[] args)
        {
            InvokeOperation<T> invoke = new InvokeOperation<T>()
            {
                pid = -1,
                rid = ++_lastInvokeId,
                tcs = new TaskCompletionSource<T>()
            };
            _invokeList.Add(invoke);

            NetDataWriter writer = new NetDataWriter();
            writer.Put((int)PacketType.invokeRequest);
            writer.Put(invoke.rid);
            writer.Put(typeof(T).FullName);
            writer.Put(method);
            writer.Put(args.Length);
            foreach (object arg in args)
            {
                if (arg != null)
                {
                    writer.Put(arg.GetType().FullName);
                    writer.Put(arg.ToJson());
                }
                else
                    writer.Put(string.Empty);
            }
            host.Send(writer, DeliveryMethod.ReliableOrdered);
            logger?.log("主机远程调用客户端" + id + "的" + method + "，参数：" + string.Join("，", args));
            _ = invokeTimeout(invoke);
            return invoke.tcs.Task;
        }
        async Task invokeTimeout(InvokeOperation invoke)
        {
            await Task.Delay((int)(timeout * 1000));
            if (_invokeList.Remove(invoke))
            {
                logger?.log("主机请求客户端" + invoke.pid + "远程调用" + invoke.rid + "超时");
                invoke.setCancel();
            }
        }
        abstract class InvokeOperation
        {
            public int rid;
            public int pid;
            public abstract void setResult(object obj);
            public abstract void setException(Exception e);
            public abstract void setCancel();
        }
        class InvokeOperation<T> : InvokeOperation
        {
            public TaskCompletionSource<T> tcs;
            public override void setResult(object obj)
            {
                if (obj == null)
                    tcs.SetResult(default);
                else if (obj is T t)
                    tcs.SetResult(t);
                else
                    throw new InvalidCastException();
            }
            public override void setException(Exception e)
            {
                tcs.SetException(e);
            }
            public override void setCancel()
            {
                tcs.SetCanceled();
            }
        }
        int _lastInvokeId = 0;
        List<InvokeOperation> _invokeList = new List<InvokeOperation>();
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod)
        {
            PacketType type = (PacketType)reader.GetInt();
            switch (type)
            {
                case PacketType.connectResponse:
                    this.id = reader.GetInt();
                    logger?.log("客户端连接主机成功，获得ID：" + this.id);
                    tcs.SetResult(this.id);
                    onConnected?.Invoke();
                    break;
                case PacketType.sendResponse:
                    try
                    {
                        int id = reader.GetInt();
                        string typeName = reader.GetString();
                        string json = reader.GetString();
                        logger?.log("客户端" + this.id + "收到主机转发的来自客户端" + id + "的数据：（" + typeName + "）" + json);
                        Type objType = Type.GetType(typeName);
                        if (objType == null)
                        {
                            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                objType = assembly.GetType(typeName);
                                if (objType != null)
                                    break;
                            }
                        }
                        object obj = BsonSerializer.Deserialize(json, objType);
                        if (tcs != null)
                            tcs.SetResult(obj);
                        onReceive?.Invoke(id, obj);
                    }
                    catch (Exception e)
                    {
                        throw e;
                    }
                    break;
                case PacketType.joinResponse:
                    var info = parseRoomInfo(peer.EndPoint, reader);
                    if (info != null)
                    {
                        logger?.log($"客户端 {id} 收到了主机的加入响应：" + info.ToJson());
                        roomInfo = info;
                        onJoinRoom?.Invoke(info);
                    }
                    break;
                case PacketType.roomInfoUpdate:
                    info = parseRoomInfo(peer.EndPoint, reader);
                    if (info != null)
                    {
                        logger?.log($"客户端 {id} 收到了主机的房间更新信息：" + info.ToJson());
                        onRoomInfoUpdate?.Invoke(roomInfo, info);
                        roomInfo = info;
                    }
                    break;
                case PacketType.invokeRequest:
                    try
                    {
                        int rid = reader.GetInt();
                        object result = null;
                        NetDataWriter writer = new NetDataWriter();
                        try
                        {
                            string returnTypeName = reader.GetString();
                            Type returnType = Type.GetType(returnTypeName);
                            if (returnType == null)
                            {
                                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                                {
                                    returnType = assembly.GetType(returnTypeName);
                                    if (returnType != null)
                                        break;
                                }
                            }
                            string methodName = reader.GetString();
                            int argLength = reader.GetInt();
                            object[] args = new object[argLength];
                            for (int i = 0; i < args.Length; i++)
                            {
                                string typeName = reader.GetString();
                                if (!string.IsNullOrEmpty(typeName))
                                {
                                    string json = reader.GetString();
                                    Type objType = Type.GetType(typeName);
                                    if (objType == null)
                                    {
                                        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                                        {
                                            objType = assembly.GetType(typeName);
                                            if (objType != null)
                                                break;
                                        }
                                    }
                                    object obj = BsonSerializer.Deserialize(json, objType);
                                    args[i] = obj;
                                }
                                else
                                    args[i] = null;
                            }
                            logger?.log("客户端" + this.id + "执行来自主机的远程调用" + rid + "，方法：" + methodName + "，参数：" + string.Join("，", args));
                            try
                            {
                                if (!tryInvoke(returnType, methodName, args, out result))
                                {
                                    throw new MissingMethodException("无法找到方法：" + returnTypeName + " " + methodName + "(" + string.Join(",", args.Select(a => a.GetType().Name)) + ")");
                                }
                            }
                            catch (Exception invokeException)
                            {
                                writer.Put((int)PacketType.invokeResponse);
                                writer.Put(rid);
                                writer.Put(invokeException.GetType().FullName);
                                string exceptionJson = invokeException.ToJson();
                                writer.Put(exceptionJson);
                                peer.Send(writer, DeliveryMethod.ReliableOrdered);
                                logger?.log("客户端" + id + "执行来自主机的远程调用" + rid + "{" + methodName + "(" + string.Join(",", args) + ")}发生异常：" + invokeException);
                                break;
                            }
                        }
                        catch (Exception e)
                        {
                            writer.Put((int)PacketType.invokeResponse);
                            writer.Put(rid);
                            writer.Put(e.GetType().FullName);
                            string exceptionJson = e.ToJson();
                            writer.Put(exceptionJson);
                            peer.Send(writer, DeliveryMethod.ReliableOrdered);
                            logger?.log("客户端" + id + "执行来自主机的远程调用" + rid + "发生异常：" + e);
                            break;
                        }
                        writer.Put((int)PacketType.invokeResponse);
                        writer.Put(rid);
                        if (result == null)
                            writer.Put(string.Empty);
                        else
                        {
                            writer.Put(result.GetType().FullName);
                            writer.Put(result.ToJson());
                        }
                        peer.Send(writer, DeliveryMethod.ReliableOrdered);
                    }
                    catch (Exception e)
                    {
                        throw e;
                    }
                    break;
                case PacketType.invokeResponse:
                    try
                    {
                        int rid = reader.GetInt();
                        string typeName = reader.GetString();
                        if (!string.IsNullOrEmpty(typeName))
                        {
                            if (TypeHelper.tryGetType(typeName, out Type objType))
                            {
                                string json = reader.GetString();
                                object obj = BsonSerializer.Deserialize(json, objType);
                                InvokeOperation invoke = _invokeList.Find(i => i.rid == rid);
                                if (invoke == null)
                                {
                                    logger?.log("主机接收到客户端" + peer.Id + "未被请求或超时的远程调用" + rid);
                                    break;
                                }
                                _invokeList.Remove(invoke);
                                if (obj is Exception e)
                                {
                                    logger?.log("主机收到客户端" + peer.Id + "的远程调用回应" + rid + "在客户端发生异常：" + e);
                                    invoke.setException(e);
                                }
                                else
                                {
                                    logger?.log("主机接收客户端" + peer.Id + "的远程调用" + rid + "返回为" + obj);
                                    invoke.setResult(obj);
                                }
                            }
                            else
                                throw new TypeLoadException("无法识别的类型" + typeName);
                        }
                        else
                        {
                            InvokeOperation invoke = _invokeList.Find(i => i.rid == rid);
                            if (invoke == null)
                            {
                                logger?.log("主机接收到客户端" + peer.Id + "未被请求或超时的远程调用" + rid);
                                break;
                            }
                            _invokeList.Remove(invoke);
                            logger?.log("主机接收客户端" + peer.Id + "的远程调用" + rid + "返回为null");
                            invoke.setResult(null);
                        }
                    }
                    catch (Exception e)
                    {
                        throw e;
                    }
                    break;
                default:
                    logger?.log("Warning", "客户端未处理的数据包类型：" + type);
                    break;
            }
        }

        private bool tryInvoke(Type returnType, string methodName, object[] args, out object result)
        {
            foreach (var target in invokeTargetList)
            {
                foreach (var method in target.GetType().GetMethods())
                {
                    if (tryInvoke(returnType, method, methodName, target, args, out result))
                        return true;
                }
            }
            result = null;
            return false;
        }
        bool tryInvoke(Type returnType, MethodInfo method, string methodName, object obj, object[] args, out object result)
        {
            if (method.ReturnType != typeof(void) && method.ReturnType != returnType)
            {
                result = null;
                return false;
            }
            if (method.Name != methodName)
            {
                result = null;
                return false;
            }
            var @params = method.GetParameters();
            if (@params.Length != args.Length)
            {
                result = null;
                return false;
            }
            for (int i = 0; i < @params.Length; i++)
            {
                if (!@params[i].ParameterType.IsInstanceOfType(args[i]))
                {
                    result = null;
                    return false;
                }
            }
            try
            {
                result = method.Invoke(obj, args);
                return true;
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException;
            }
        }
        List<object> invokeTargetList { get; } = new List<object>();
        public void addInvokeTarget(object obj)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));
            if (!invokeTargetList.Contains(obj))
                invokeTargetList.Add(obj);
        }
        public bool removeInvokeTarget(object obj)
        {
            return invokeTargetList.Remove(obj);
        }
        public event Action<int, object> onReceive;
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }
        public void disconnect()
        {
            if (tcs != null)
            {
                tcs.SetCanceled();
                tcs = null;
            }
            if (host != null)
            {
                host.Disconnect();
                host = null;
            }
        }
        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            logger?.log("客户端" + id + "与主机断开连接，原因：" + disconnectInfo.Reason + "，SocketErrorCode：" + disconnectInfo.SocketErrorCode);
            if (tcs != null)
            {
                tcs.SetCanceled();
                tcs = null;
            }
            host = null;
            onDisconnect?.Invoke();
            onQuitRoom?.Invoke();
        }
        public event Action onDisconnect;
        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            logger?.log("Error", "客户端" + id + "与" + endPoint + "发生网络异常：" + socketError);
        }
        public void OnConnectionRequest(ConnectionRequest request)
        {
            throw new NotImplementedException();
        }


        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            switch (messageType)
            {
                case UnconnectedMessageType.BasicMessage:
                    if (reader.GetInt() == (int)PacketType.discoveryResponse)
                    {
                        uint reqID = reader.GetUInt();
                        var roomInfo = parseRoomInfo(remoteEndPoint, reader);
                        if (reqID == 0)
                        {
                            logger?.log($"客户端找到主机，{remoteEndPoint.Address}:{remoteEndPoint.Port}");
                            if (roomInfo != null) onRoomFound?.Invoke(roomInfo);
                        }
                        else
                        {
                            logger?.log($"获取到主机 {remoteEndPoint.Address}:{remoteEndPoint.Port} 更新的房间信息。");
                            if (roomCheckTasks.ContainsKey(reqID))
                            {
                                roomCheckTasks[reqID].SetResult(roomInfo);
                                roomCheckTasks.Remove(reqID);
                            }
                            else
                            {
                                logger?.log($"RequestID {reqID} 不存在。");
                            }
                        }
                    }
                    else
                    {
                        logger?.log("消息类型不匹配");
                    }
                    break;
                default:
                    break;
            }
        }
        public void stop()
        {
            net.Stop();
        }
        #region Room
        /// <summary>
        /// 局域网发现是Host收到了给回应，你不可能知道Host什么时候回应，也不知道局域网里有多少个可能会回应的Host，所以这里不返回任何东西。
        /// </summary>
        /// <param name="port">搜索端口。默认9050</param>
        public void findRoom(int port = 9050)
        {
            var writer = roomDiscoveryRequestWriter(0);
            net.SendBroadcast(writer, port);
        }
        RoomInfo parseRoomInfo(IPEndPoint remoteEndPoint, NetPacketReader reader)
        {
            var type = reader.GetString();
            var json = reader.GetString();
            Type objType = Type.GetType(type);
            if (objType == null)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    objType = assembly.GetType(type);
                    if (objType != null)
                        break;
                }
            }
            object obj = BsonSerializer.Deserialize(json, objType);
            if (obj is RoomInfo info)
            {
                info.ip = remoteEndPoint.Address.ToString();
                info.port = remoteEndPoint.Port;
                return info;
            }
            else
            {
                logger?.log($"主机房间信息类型错误，收到了 {type}");
                return null;
            }
        }
        NetDataWriter roomDiscoveryRequestWriter(uint reqID)
        {
            NetDataWriter writer = new NetDataWriter();
            writer.Put((int)PacketType.discoveryRequest);
            writer.Put(reqID);
            return writer;
        }

        Dictionary<uint, TaskCompletionSource<RoomInfo>> roomCheckTasks = new Dictionary<uint, TaskCompletionSource<RoomInfo>>();

        public event Action<RoomInfo> onRoomFound;

        void taskChecker(uint id)
        {
            Thread.Sleep(5000);
            logger?.log("操作超时");
            if (roomCheckTasks.ContainsKey(id) && !roomCheckTasks[id].Task.IsCompleted)
            {
                roomCheckTasks[id].SetResult(null);
                roomCheckTasks.Remove(id);
            }
        }

        /// <summary>
        /// 向目标房间请求新的房间信息，如果目标房间已经不存在了，那么会返回空，否则返回更新的房间信息。
        /// </summary>
        /// <param name="roomInfo"></param>
        /// <returns></returns>
        public Task<RoomInfo> checkRoomInfo(RoomInfo roomInfo)
        {
            uint reqID;
            var random = new System.Random();

            do { reqID = (uint)random.Next(); }
            while (roomCheckTasks.ContainsKey(reqID) || reqID == 0);

            NetDataWriter writer = roomDiscoveryRequestWriter(reqID);
            var result = net.SendUnconnectedMessage(writer, new IPEndPoint(IPAddress.Parse(roomInfo.ip), roomInfo.port));

            TaskCompletionSource<RoomInfo> task = new TaskCompletionSource<RoomInfo>();
            if (result)
            {
                roomCheckTasks.Add(reqID, task);
                var t = new Task(() => taskChecker(reqID));
                t.Start();
            }
            else
            {
                task.SetResult(null);
            }
            return task.Task;
        }
        public event Action onQuitRoom;
        public event Action<RoomInfo> onJoinRoom;
        /// <summary>
        /// 加入指定房间，你必须告诉房主你的个人信息。
        /// </summary>
        /// <param name="room"></param>
        /// <param name="playerInfo"></param>
        /// <returns></returns>
        public async Task joinRoom(RoomInfo room, RoomPlayerInfo playerInfo)
        {
            var id = await join(room.ip, room.port);
            if (id == -1)
                throw new TimeoutException();
            playerInfo.id = id;
            send(playerInfo as object, PacketType.joinRequest);
        }
        /// <summary>
        /// 当前所在房间信息，如果不在任何房间中则为空。
        /// </summary>
        public RoomInfo roomInfo
        {
            get; private set;
        }
        public delegate void RoomInfoUpdateDelegate(RoomInfo now, RoomInfo updated);
        public event RoomInfoUpdateDelegate onRoomInfoUpdate;
        public void quitRoom()
        {
            if (host != null)
                host.Disconnect();
        }
        #endregion
    }

    [Serializable]
    public class RPCException : Exception
    {
        public RPCException() { }
        public RPCException(string message) : base(message) { }
        public RPCException(string message, Exception inner) : base(message, inner) { }
        protected RPCException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
