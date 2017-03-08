namespace System.Buffers
{
    internal static class BufferExtensions
    {
        public static ReadOnlySpan<byte> ToSingleSpan(this ReadOnlyBytes self)
        {
            if (self.Rest == null)
            {
                return self.First.Span;
            }
            else
            {
                return self.ToSpan();
            }
        }

        public static ReadOnlyBytes? TryReadBytes(this BytesReader self, int count)
        {
            try
            {
                return self.ReadBytes(count);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}
