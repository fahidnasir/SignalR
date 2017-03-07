// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;

namespace Microsoft.AspNetCore.Sockets.Internal.Formatters
{
    public class MessageFormatter
    {
        private TextMessageFormatter _textFormatter = new TextMessageFormatter();
        private BinaryMessageFormatter _binaryFormatter = new BinaryMessageFormatter();

        public void Reset()
        {
            _textFormatter.Reset();
            _binaryFormatter.Reset();
        }

        public bool TryWriteMessage(Message message, IOutput output, MessageFormat format)
        {
            if (!message.EndOfMessage)
            {
                // This is a truely exceptional condition since we EXPECT callers to have already
                // buffered incomplete messages and synthesized the correct, complete message before
                // giving it to us. Hence we throw, instead of returning false.
                throw new InvalidOperationException("Cannot format message where endOfMessage is false using this format");
            }

            return format == MessageFormat.Text ?
                _textFormatter.TryWriteMessage(message, output) :
                _binaryFormatter.TryWriteMessage(message, output);
        }

        public bool TryParseMessage(ref BytesReader buffer, MessageFormat format, out Message message)
        {
            return format == MessageFormat.Text ?
                _textFormatter.TryParseMessage(ref buffer, out message) :
                _binaryFormatter.TryParseMessage(ref buffer, out message);
        }
    }
}
