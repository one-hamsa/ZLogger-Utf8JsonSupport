using System;
using System.Collections.Generic;
using System.Text;

namespace Cysharp.Text
{
    public class ZLoggerFormatException : FormatException
    {
        public readonly string format;

        public ZLoggerFormatException(string message, string format) : base(message)
        {
            this.format = format;
        }

        public override string ToString()
        {
            return base.ToString() + Environment.NewLine + "Format: " + format;
        }
    }
    
    internal static class ExceptionUtil
    {
        internal static void ThrowArgumentException(string paramName)
        {
            throw new ArgumentException("Can't format argument.", paramName);
        }

        internal static void ThrowFormatException(string format)
        {
            throw new ZLoggerFormatException("Index (zero based) must be greater than or equal to zero and less than the size of the argument list.", format);
        }

        internal static void ThrowFormatError(string format)
        {
            throw new ZLoggerFormatException("Input string was not in a correct format.", format);
        }
    }
}
