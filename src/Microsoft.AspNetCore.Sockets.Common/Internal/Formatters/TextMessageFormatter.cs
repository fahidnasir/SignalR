// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Binary;
using System.Buffers;
using System.Text;
using System.Text.Formatting;

namespace Microsoft.AspNetCore.Sockets.Internal.Formatters
{
    internal class TextMessageFormatter
    {
        private const char FieldDelimiter = ':';
        private const char MessageDelimiter = ';';
        private const char TextTypeFlag = 'T';
        private const char BinaryTypeFlag = 'B';

        private const char CloseTypeFlag = 'C';
        private const char ErrorTypeFlag = 'E';

        private ParserState _state;

        public void Reset()
        {
            _state = default(ParserState);
        }

        public bool TryWriteMessage(Message message, IOutput output)
        {
            // Calculate the length, it's the number of characters for text messages, but number of base64 characters for binary
            var length = message.Payload.Length;
            if (message.Type == MessageType.Binary)
            {
                length = Base64.ComputeEncodedLength(length);
            }

            // Get the type indicator
            if (!TryGetTypeIndicator(message.Type, out var typeIndicator))
            {
                return false;
            }

            // Write the length as a string
            output.Append(length, TextEncoder.Utf8);

            // Write the field delimiter ':'
            output.Append(FieldDelimiter, TextEncoder.Utf8);

            // Write the type
            output.Append(typeIndicator, TextEncoder.Utf8);

            // Write the field delimiter ':'
            output.Append(FieldDelimiter, TextEncoder.Utf8);

            // Write the payload
            if (!TryWritePayload(message, output, length))
            {
                return false;
            }

            // Terminator
            output.Append(MessageDelimiter, TextEncoder.Utf8);
            return true;
        }

        /// <summary>
        /// Attempts to parse a message from the buffer. Returns 'false' if there is not enough data to complete a message. Throws an
        /// exception if there is a format error in the provided data.
        /// </summary>
        public bool TryParseMessage(ref BytesReader buffer, out Message message)
        {
            while (buffer.Unread.Length > 0)
            {
                switch (_state.Phase)
                {
                    case ParsePhase.ReadingLength:
                        if (!TryReadLength(ref buffer))
                        {
                            message = default(Message);
                            return false;
                        }

                        break;
                    case ParsePhase.LengthComplete:
                        if (!TryReadDelimiter(ref buffer, ParsePhase.ReadingType, "length"))
                        {
                            message = default(Message);
                            return false;
                        }

                        break;
                    case ParsePhase.ReadingType:
                        if (!TryReadType(ref buffer))
                        {
                            message = default(Message);
                            return false;
                        }

                        break;
                    case ParsePhase.TypeComplete:
                        if (!TryReadDelimiter(ref buffer, ParsePhase.ReadingPayload, "type"))
                        {
                            message = default(Message);
                            return false;
                        }

                        break;
                    case ParsePhase.ReadingPayload:
                        if (TryReadPayload(ref buffer, out message))
                        {
                            return true;
                        }
                        break;
                    default:
                        throw new InvalidOperationException($"Invalid parser phase: {_state.Phase}");
                }
            }

            message = default(Message);
            return false;
        }

        private bool TryReadLength(ref BytesReader buffer)
        {
            // Read until the first ':' to find the length
            var lengthSpan = buffer.ReadBytesUntil((byte)FieldDelimiter)?.ToSingleSpan();
            if (lengthSpan == null)
            {
                // Insufficient data
                return false;
            }

            // Parse the length
            if (!PrimitiveParser.TryParseInt32(lengthSpan.Value, out var length, out var consumedByLength, encoder: TextEncoder.Utf8) || consumedByLength < lengthSpan.Length)
            {
                if (TextEncoder.Utf8.TryDecode(lengthSpan.Value, out var lengthString, out _))
                {
                    throw new FormatException($"Invalid length: '{lengthString}'");
                }

                throw new FormatException("Invalid length!");
            }

            _state.Length = length;
            _state.Phase = ParsePhase.LengthComplete;
            return true;
        }

        private bool TryReadDelimiter(ref BytesReader buffer, ParsePhase nextPhase, string field)
        {
            if (buffer.Unread.Length == 0)
            {
                return false;
            }

            if (buffer.Unread[0] != FieldDelimiter)
            {
                throw new FormatException($"Missing field delimiter ':' after {field}");
            }
            buffer.Advance(1);

            _state.Phase = nextPhase;
            return true;
        }

        private bool TryReadType(ref BytesReader buffer)
        {
            if (buffer.Unread.Length == 0)
            {
                return false;
            }

            if (!TryParseType(buffer.Unread[0], out _state.MessageType))
            {
                throw new FormatException($"Unknown message type: '{(char)buffer.Unread[0]}'");
            }

            buffer.Advance(1);
            _state.Phase = ParsePhase.TypeComplete;
            return true;
        }

        private bool TryReadPayload(ref BytesReader buffer, out Message message)
        {
            if (_state.Payload == null)
            {
                _state.Payload = new byte[_state.Length];
            }

            if (_state.Read >= _state.Length)
            {
                byte[] payload = ProducePayload();

                // We're done!
                message = new Message(payload, _state.MessageType);
                Reset();
                return true;
            }

            // Copy as much as possible from the Unread buffer
            var toCopy = Math.Min(_state.Length, buffer.Unread.Length);
            buffer.Unread.Slice(0, toCopy).CopyTo(_state.Payload.Slice(_state.Read));
            _state.Read += toCopy;
            buffer.Advance(toCopy);

            message = default(Message);
            return false;
        }

        private byte[] ProducePayload()
        {
            var payload = _state.Payload;
            if (_state.MessageType == MessageType.Binary && payload.Length > 0)
            {
                // Determine the output size
                // Every 4 Base64 characters represents 3 bytes
                var decodedLength = (payload.Length / 4) * 3;

                // Subtract padding bytes
                if (payload[payload.Length - 1] == '=')
                {
                    decodedLength -= 1;
                }
                if (payload.Length > 1 && payload[payload.Length - 2] == '=')
                {
                    decodedLength -= 1;
                }

                // Allocate a new buffer to decode to
                var decodeBuffer = new byte[decodedLength];
                if (Base64.Decode(payload, decodeBuffer) != decodedLength)
                {
                    throw new FormatException("Invalid Base64 payload");
                }
                payload = decodeBuffer;
            }

            return payload;
        }

        private static bool TryWritePayload(Message message, IOutput output, int length)
        {
            // Payload
            if (message.Type == MessageType.Binary)
            {
                // TODO: Base64 writer that works with IOutput would be amazing!
                var arr = new byte[Base64.ComputeEncodedLength(message.Payload.Length)];
                Base64.Encode(message.Payload, arr);
                return output.TryWrite(arr);
            }
            else
            {
                return output.TryWrite(message.Payload);
            }
        }

        private static bool TryParseType(byte type, out MessageType messageType)
        {
            switch ((char)type)
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

        private static bool TryGetTypeIndicator(MessageType type, out char typeIndicator)
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
                    typeIndicator = '\0';
                    return false;
            }
        }

        private struct ParserState
        {
            public ParsePhase Phase;
            public int Length;
            public MessageType MessageType;
            public byte[] Payload;
            public int Read;
        }

        private enum ParsePhase
        {
            ReadingLength = 0,
            LengthComplete,
            ReadingType,
            TypeComplete,
            ReadingPayload
        }
    }
}
