using System;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Session;
using EasyRDP.Core.Transport;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// 客户端输入会话。捕获 WPF 键盘/鼠标事件并发送给服务端。
    /// </summary>
    public class ClientInputSession : IClientInputSession
    {
        private ITransportClient _transport;
        private int _screenWidth;
        private int _screenHeight;
        private bool _disposed;
        private uint _sendFrameId = 1000; // Separate from stream frame IDs

        public void Start(ITransportClient transport, int screenWidth, int screenHeight)
        {
            _transport = transport;
            _screenWidth = screenWidth;
            _screenHeight = screenHeight;
        }

        /// <summary>服务端分辨率变化通知，更新坐标映射。</summary>
        public void OnResolutionChanged(int newWidth, int newHeight)
        {
            _screenWidth = newWidth;
            _screenHeight = newHeight;
        }

        public void Stop()
        {
            _disposed = true;
            _transport = null;
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>发送输入事件到服务端。</summary>
        public void SendInput(InputEventMessage msg)
        {
            if (_disposed || _transport == null) return;

            byte[] payload = msg.Pack();
            MessageReassembler.FragAndSend(
                _sendFrameId++, (byte)MessageType.InputEvent, payload,
                (sid, data) => _transport.Send(data), 0);
        }

        /// <summary>把客户端控件坐标映射到服务端屏幕坐标。</summary>
        public void MapCoordinates(double controlX, double controlY, double controlW, double controlH,
            out int serverX, out int serverY)
        {
            if (_screenWidth <= 0 || _screenHeight <= 0 || controlW <= 0 || controlH <= 0)
            {
                serverX = (int)controlX;
                serverY = (int)controlY;
                return;
            }
            serverX = (int)(controlX / controlW * _screenWidth);
            serverY = (int)(controlY / controlH * _screenHeight);
        }
    }
}
