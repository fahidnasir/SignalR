// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;

namespace Microsoft.AspNetCore.Sockets.Internal.Formatters
{
    internal static class BinaryMessageFormatter
    {
        internal const byte TextTypeFlag = 0x00;
        internal const byte BinaryTypeFlag = 0x01;
        internal const byte ErrorTypeFlag = 0x02;
        internal const byte CloseTypeFlag = 0x03;

        public static bool TryWriteMessage(Message message, IOutput output)
        {
            if (!TryGetTypeIndicator(message.Type, out var typeIndicator))
            {
                return false;
            }

            // Try to write the data
            if (!output.TryWriteBigEndian((long)message.Payload.Length))
            {
                return false;
            }

            if (!output.TryWriteBigEndian(typeIndicator))
            {
                return false;
            }

            if (!output.TryWrite(message.Payload))
            {
                return false;
            }

            return true;
        }

        private static bool TryGetTypeIndicator(MessageType type, out byte typeIndicator)
        {
            switch (type)
            {
                case MessageType.Text:
                    typeIndicator = TextTypeFlag;
                    return true;
                case MessageType.Binary:
                    typeIndicator = BinaryTypeFlag;
                    return true;
                case MessageType.Close:
                    typeIndicator = CloseTypeFlag;
                    return true;
                case MessageType.Error:
                    typeIndicator = ErrorTypeFlag;
                    return true;
                default:
                    typeIndicator = 0;
                    return false;
            }
        }
    }
}