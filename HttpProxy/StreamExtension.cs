using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace HttpProxy
{
    internal static class StreamExtension
    {
        internal static Task WriteLineAsync(this Stream stream)
        {
            return WriteLineAsync(stream, string.Empty);
        }

        internal static Task WriteLineAsync(this Stream stream, string content)
        {
            return WriteLineAsync(stream, content, Encoding.ASCII);
        }

        internal static async Task WriteLineAsync(this Stream stream, string content, Encoding encoding)
        {
            var buffer = encoding.GetBytes(content + "\r\n");
            await stream.WriteAsync(buffer, 0, buffer.Length);
        }

        internal static void ReadLine(this Stream stream, StringBuilder buffer, Encoding encoding)
        {
            using (var reader = new BinaryReader(stream, encoding, true))
            {
                char ch;
                do
                {
                    ch = reader.ReadChar();
                    buffer.Append(ch);
                } while (ch != '\n');
            }

            // Remove tailing newline
            if (buffer[buffer.Length - 2] == '\r')
            {
                buffer.Remove(buffer.Length - 2, 2);
            }
            else
            {
                buffer.Remove(buffer.Length - 1, 1);
            }
        }

        internal static string ReadLine(this Stream stream, Encoding encoding)
        {
            var buffer = new StringBuilder();
            ReadLine(stream, buffer, encoding);
            return buffer.ToString();
        }

        internal static string ReadLineASCII(this Stream stream)
        {
            return ReadLine(stream, Encoding.ASCII);
        }
    }
}