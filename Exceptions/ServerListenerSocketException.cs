﻿using System;

namespace net.vieapps.Components.WebSockets.Exceptions
{
    [Serializable]
    public class ServerListenerSocketException : Exception
    {
        public ServerListenerSocketException() : base() { }

        public ServerListenerSocketException(string message) : base(message) { }

        public ServerListenerSocketException(string message, Exception inner) : base(message, inner) { }
    }
}
