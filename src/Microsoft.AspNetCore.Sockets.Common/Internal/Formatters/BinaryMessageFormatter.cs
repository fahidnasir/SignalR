// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Binary;
using System.Buffers;
using System.IO.Pipelines;

namespace Microsoft.AspNetCore.Sockets.Internal.Formatters
{
    internal class BinaryMessageFormatter
    {
        private const byte TextTypeFlag = 0x00;
        private const byte BinaryTypeFlag = 0x01;
        private const byte ErrorTypeFlag = 0x02;
        private const byte CloseTypeFlag = 0x03;

        private ParserState _state;

        public void Reset()
        {
            _state = default(ParserState);
        }

        public bool TryWriteMessage(Message message, IOutput output)
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

        public bool TryParseMessage(ref BytesReader buffer, out Message message)
        {
            // TODO: Single-span optimizations?
            if (_state.Length == null)
            {
                var length = buffer.ReadBytes(sizeof(long)).ToSingleSpan();
                if (length.Length < sizeof(long))
                {
                    message = default(Message);
                    return false;
                }

                var longLength = length.ReadBigEndian<long>();
                if (longLength > Int32.MaxValue)
                {
                    throw new FormatException("Messages over 2GB in size are not supported");
                }
                _state.Length = (int)longLength;
            }

            if (_state.MessageType == null)
            {
                if (buffer.Unread.Length == 0)
                {
                    message = default(Message);
                    return false;
                }

                var typeByte = buffer.Unread[0];

                if (!TryParseType(typeByte, out var messageType))
                {
                    throw new FormatException($"Unknown type value: 0x{typeByte:X}");
                }

                buffer.Advance(1);
            }

            if(_state.Payload == null)
            {
                _state.Payload = new byte[_state.Length.Value];
            }

            while (_state.Read < _state.Payload.Length && buffer.Unread.Length > 0)
            {
                // Copy what we can from the current unread segment
                var toCopy = Math.Min(_state.Payload.Length - _state.Read, buffer.Unread.Length);
                buffer.Unread.Slice(0, toCopy).CopyTo(_state.Payload.Slice(_state.Read));
                _state.Read += buffer.Unread.Length;
                buffer.Advance(buffer.Unread.Length);
            }

            if(_state.Read == _state.Payload.Length)
            {
                message = new Message(_state.Payload, _state.MessageType);
                return true;
            }

            message = default(Message);
            return false;
        }

        private static bool TryParseType(byte type, out MessageType messageType)
        {
            switch (type)
            {
                case TextTypeFlag:
                    messageType = MessageType.Text;
                    return true;
                case BinaryTypeFlag:
                    messageType = MessageType.Binary;
                    return true;
                case CloseTypeFlag:
                    messageType = MessageType.Close;
                    return true;
                case ErrorTypeFlag:
                    messageType = MessageType.Error;
                    return true;
                default:
                    messageType = default(MessageType);
                    return false;
            }
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

        private struct ParserState
        {
            public int? Length;
            public MessageType? MessageType;
            public byte[] Payload;
            public int Read;
        }
    }
}