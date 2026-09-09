using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BinaryToTextEncoding
{
    /// <summary>
    /// Base93 编码解码核心（字符集：ASCII 33-126 排除 '='，填充字符 '='）
    /// </summary>
    public static class Base93
    {
        private static readonly char[] Charset;
        private static readonly int[] CharToIndex;
        private const char PadChar = '=';

        static Base93()
        {
            // 构建字符集：ASCII 33-126 中排除 61 ('=')
            var list = new List<char>();
            for (int i = 33; i <= 126; i++)
            {
                if (i != 61) list.Add((char)i);
            }
            Charset = list.ToArray(); // 共93个字符

            // 构建反向查找表
            CharToIndex = new int[128];
            for (int i = 0; i < 128; i++) CharToIndex[i] = -1;
            for (int i = 0; i < Charset.Length; i++)
            {
                CharToIndex[Charset[i]] = i;
            }
        }

        /// <summary>
        /// 将4字节编码为5个Base93字符
        /// </summary>
        public static string Encode4Bytes(byte[] bytes, int offset = 0)
        {
            uint value = (uint)((bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3]);
            int[] digits = new int[5];
            for (int i = 4; i >= 0; i--)
            {
                digits[i] = (int)(value % 93);
                value /= 93;
            }
            // 【修复】原代码此处多了一次 Array.Reverse：循环从 i=4 递减填充，
            // digits[0] 已是最高位（93^4），digits[4] 为最低位（93^0），
            // 直接映射即得高位在前的字符序列（与 DecodeBlock 的 value = value*93 + digit 一致）。
            // 多余的 Reverse 导致编码输出低位在前，解码端按高位在前解析，往返必损坏。
            char[] result = new char[5];
            for (int i = 0; i < 5; i++)
            {
                result[i] = Charset[digits[i]];
            }
            return new string(result);
        }

        /// <summary>
        /// 将1-3字节编码为5个Base93字符（带填充）
        /// </summary>
        public static string EncodeRemaining(byte[] bytes)
        {
            int n = bytes.Length; // 1-3
            uint value;
            int m; // 有效字符数
            if (n == 1)
            {
                value = bytes[0];
                m = 2;
            }
            else if (n == 2)
            {
                value = (uint)((bytes[0] << 8) | bytes[1]);
                m = 3;
            }
            else // n==3
            {
                value = (uint)((bytes[0] << 16) | (bytes[1] << 8) | bytes[2]);
                m = 4;
            }

            int[] digits = new int[m];
            for (int i = m - 1; i >= 0; i--)
            {
                digits[i] = (int)(value % 93);
                value /= 93;
            }
            // 【修复】与 Encode4Bytes 相同：循环后 digits[0] 已为最高位，
            // 原 Array.Reverse 导致低位在前输出，与解码端不一致，往返必损坏。
            char[] result = new char[5];
            for (int i = 0; i < m; i++)
            {
                result[i] = Charset[digits[i]];
            }
            for (int i = m; i < 5; i++)
            {
                result[i] = PadChar;
            }
            return new string(result);
        }

        /// <summary>
        /// 解码5个Base93字符为字节数组（可能返回1-4字节）
        /// </summary>
        public static byte[] DecodeBlock(char[] block)
        {
            if (block.Length != 5) throw new ArgumentException("Block must be 5 characters");

            int[] digits = new int[5];
            int padCount = 0;
            for (int i = 0; i < 5; i++)
            {
                if (block[i] == PadChar)
                {
                    padCount++;
                    digits[i] = -1;
                }
                else
                {
                    digits[i] = CharToIndex[block[i]];
                    if (digits[i] == -1) throw new InvalidDataException($"Invalid character '{block[i]}' in block");
                }
            }

            int valid = 5 - padCount;
            int byteCount;
            if (valid == 5) byteCount = 4;
            else if (valid == 4) byteCount = 3;
            else if (valid == 3) byteCount = 2;
            else if (valid == 2) byteCount = 1;
            else throw new InvalidDataException("Invalid padding");

            // 计算数值（高位在前）
            uint value = 0;
            for (int i = 0; i < valid; i++)
            {
                value = value * 93 + (uint)digits[i];
            }

            byte[] result = new byte[byteCount];
            for (int i = 0; i < byteCount; i++)
            {
                int shift = (byteCount - 1 - i) * 8;
                result[i] = (byte)((value >> shift) & 0xFF);
            }
            return result;
        }
    }

    /// <summary>
    /// 编码器：将二进制流压缩并转换为Base93文本流
    /// </summary>
    internal class Encoder : IDisposable
    {
        private readonly Stream _inputStream;
        private readonly Stream _outputStream;
        private readonly bool _leaveOpen;
        private readonly BlockingCollection<byte[]> _compressedQueue = new();
        private readonly Task _compressTask;
        private readonly Task _encodeTask;
        private Exception _compressException;
        private Exception _encodeException;

        public Encoder(Stream inputStream, Stream outputStream, bool leaveOpen = false)
        {
            _inputStream = inputStream;
            _outputStream = outputStream;
            _leaveOpen = leaveOpen;
            _compressTask = Task.Run(CompressThread);
            _encodeTask = Task.Run(EncodeThread);
        }

        private void CompressThread()
        {
            try
            {
                using (var memStream = new MemoryStream())
                {
                    var deflateStream = new DeflateStream(memStream, CompressionMode.Compress, true);
                    byte[] buffer = new byte[81920]; // 80KB
                    int bytesRead;
                    long lastReadPos = 0;

                    while ((bytesRead = _inputStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        deflateStream.Write(buffer, 0, bytesRead);
                        deflateStream.Flush();

                        // 读取新产生的压缩数据
                        memStream.Position = lastReadPos;
                        int newLength = (int)(memStream.Length - lastReadPos);
                        if (newLength > 0)
                        {
                            byte[] newData = new byte[newLength];
                            memStream.Read(newData, 0, newLength);
                            _compressedQueue.Add(newData);
                            lastReadPos = memStream.Length;
                        }
                    }

                    // 完成压缩
                    deflateStream.Close();
                    memStream.Position = lastReadPos;
                    int finalLength = (int)(memStream.Length - lastReadPos);
                    if (finalLength > 0)
                    {
                        byte[] finalData = new byte[finalLength];
                        memStream.Read(finalData, 0, finalLength);
                        _compressedQueue.Add(finalData);
                    }
                }
                _compressedQueue.CompleteAdding();
            }
            catch (Exception ex)
            {
                _compressException = ex;
                _compressedQueue.CompleteAdding(); // 避免编码线程永远等待
            }
        }

        private void EncodeThread()
        {
            try
            {
                var buffer = new Queue<byte>(); // 待编码的字节队列
                var writer = new StreamWriter(_outputStream, Encoding.ASCII, 4096, true); // 保持流打开

                foreach (var data in _compressedQueue.GetConsumingEnumerable())
                {
                    foreach (byte b in data)
                    {
                        buffer.Enqueue(b);
                    }

                    while (buffer.Count >= 4)
                    {
                        byte[] four = new byte[4];
                        for (int i = 0; i < 4; i++) four[i] = buffer.Dequeue();
                        string encoded = Base93.Encode4Bytes(four);
                        writer.Write(encoded);
                        writer.Flush(); // 确保立即写入输出流
                    }
                }

                // 处理剩余字节
                if (buffer.Count > 0)
                {
                    byte[] remaining = buffer.ToArray();
                    string encoded = Base93.EncodeRemaining(remaining);
                    writer.Write(encoded);
                    writer.Flush();
                }
            }
            catch (Exception ex)
            {
                _encodeException = ex;
            }
        }

        public void WaitForCompletion()
        {
            Task.WaitAll(_compressTask, _encodeTask);
            if (_compressException != null) throw new AggregateException(_compressException);
            if (_encodeException != null) throw new AggregateException(_encodeException);
        }

        public void Dispose()
        {
            _compressedQueue.Dispose();
            if (!_leaveOpen)
            {
                _inputStream?.Dispose();
                _outputStream?.Dispose();
            }
        }
    }

    /// <summary>
    /// 自定义流，从BlockingCollection中读取字节数组
    /// </summary>
    internal class BlockingCollectionStream : Stream
    {
        private readonly BlockingCollection<byte[]> _queue;
        private byte[] _currentBuffer;
        private int _currentOffset;
        private bool _completed;

        public BlockingCollectionStream(BlockingCollection<byte[]> queue)
        {
            _queue = queue;
            _currentBuffer = null;
            _currentOffset = 0;
            _completed = false;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_completed) return 0;

            // 如果当前缓冲区用完，取下一个
            if (_currentBuffer == null || _currentOffset >= _currentBuffer.Length)
            {
                if (!_queue.TryTake(out _currentBuffer, Timeout.Infinite))
                {
                    // 队列已完成且无数据
                    _completed = true;
                    return 0;
                }
                _currentOffset = 0;
            }

            int bytesToCopy = Math.Min(count, _currentBuffer.Length - _currentOffset);
            Array.Copy(_currentBuffer, _currentOffset, buffer, offset, bytesToCopy);
            _currentOffset += bytesToCopy;
            return bytesToCopy;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// 解码器：将Base93文本流解码并解压为原始二进制流
    /// </summary>
    internal class Decoder : IDisposable
    {
        private readonly Stream _inputStream;
        private readonly Stream _outputStream;
        private readonly bool _leaveOpen;
        private readonly BlockingCollection<byte[]> _decodedQueue = new();
        private readonly Task _decodeTask;
        private readonly Task _decompressTask;
        private Exception _decodeException;
        private Exception _decompressException;

        public Decoder(Stream inputStream, Stream outputStream, bool leaveOpen = false)
        {
            _inputStream = inputStream;
            _outputStream = outputStream;
            _leaveOpen = leaveOpen;
            _decodeTask = Task.Run(DecodeThread);
            _decompressTask = Task.Run(DecompressThread);
        }

        private void DecodeThread()
        {
            try
            {
                using (var reader = new StreamReader(_inputStream, Encoding.ASCII, false, 4096, true))
                {
                    var buffer = new List<char>();
                    char[] temp = new char[4096];
                    int charsRead;

                    while ((charsRead = reader.Read(temp, 0, temp.Length)) > 0)
                    {
                        buffer.AddRange(temp.Take(charsRead));
                        while (buffer.Count >= 5)
                        {
                            char[] block = buffer.Take(5).ToArray();
                            buffer.RemoveRange(0, 5);
                            byte[] bytes = Base93.DecodeBlock(block);
                            _decodedQueue.Add(bytes);
                        }
                    }

                    if (buffer.Count > 0)
                    {
                        throw new InvalidDataException("Input text length is not a multiple of 5");
                    }
                }
                _decodedQueue.CompleteAdding();
            }
            catch (Exception ex)
            {
                _decodeException = ex;
                _decodedQueue.CompleteAdding();
            }
        }

        private void DecompressThread()
        {
            try
            {
                var inputStream = new BlockingCollectionStream(_decodedQueue);
                var deflateStream = new DeflateStream(inputStream, CompressionMode.Decompress);
                deflateStream.CopyTo(_outputStream);
                _outputStream.Flush();
            }
            catch (Exception ex)
            {
                _decompressException = ex;
            }
        }

        public void WaitForCompletion()
        {
            Task.WaitAll(_decodeTask, _decompressTask);
            if (_decodeException != null) throw new AggregateException(_decodeException);
            if (_decompressException != null) throw new AggregateException(_decompressException);
        }

        public void Dispose()
        {
            _decodedQueue.Dispose();
            if (!_leaveOpen)
            {
                _inputStream?.Dispose();
                _outputStream?.Dispose();
            }
        }
    }

    /// <summary>
    /// 公开的编解码API，供应用内部调用
    /// </summary>
    public static class BinaryTextCodec
    {
        /// <summary>
        /// 将输入流中的二进制数据压缩并编码为Base93文本，写入输出流。
        /// </summary>
        /// <param name="input">原始二进制输入流</param>
        /// <param name="output">编码后的文本输出流（ASCII文本）</param>
        /// <param name="leaveOpen">如果为true，则方法返回时不关闭输入/输出流</param>
        public static void Encode(Stream input, Stream output, bool leaveOpen = false)
        {
            var encoder = new Encoder(input, output, leaveOpen);
            encoder.WaitForCompletion();
        }

        /// <summary>
        /// 将输入流中的Base93文本解码并解压为原始二进制，写入输出流。
        /// </summary>
        /// <param name="input">Base93文本输入流（ASCII文本）</param>
        /// <param name="output">原始二进制输出流</param>
        /// <param name="leaveOpen">如果为true，则方法返回时不关闭输入/输出流</param>
        public static void Decode(Stream input, Stream output, bool leaveOpen = false)
        {
            var decoder = new Decoder(input, output, leaveOpen);
            decoder.WaitForCompletion();
        }

        /// <summary>
        /// 将字节数组编码为Base93文本字节数组（ASCII编码）
        /// </summary>
        public static byte[] Encode(byte[] data)
        {
            var input = new MemoryStream(data);
            var output = new MemoryStream();
            Encode(input, output, false);
            return output.ToArray();
        }

        /// <summary>
        /// 将Base93文本字节数组解码为原始二进制字节数组
        /// </summary>
        public static byte[] Decode(byte[] data)
        {
            var input = new MemoryStream(data);
            var output = new MemoryStream();
            Decode(input, output, false);
            return output.ToArray();
        }

        /// <summary>
        /// 将字节数组编码为Base93字符串
        /// </summary>
        public static string EncodeToString(byte[] data)
        {
            byte[] encodedBytes = Encode(data);
            return Encoding.ASCII.GetString(encodedBytes);
        }

        /// <summary>
        /// 将Base93字符串解码为原始二进制字节数组
        /// </summary>
        public static byte[] DecodeFromString(string text)
        {
            byte[] inputBytes = Encoding.ASCII.GetBytes(text);
            return Decode(inputBytes);
        }

        /// <summary>
        /// 将文件编码为Base93文本文件
        /// </summary>
        public static void EncodeFile(string inputPath, string outputPath)
        {
            var input = File.OpenRead(inputPath);
            var output = File.Create(outputPath);
            Encode(input, output, false);
        }

        /// <summary>
        /// 将Base93文本文件解码为原始二进制文件
        /// </summary>
        public static void DecodeFile(string inputPath, string outputPath)
        {
            var input = File.OpenRead(inputPath);
            var output = File.Create(outputPath);
            Decode(input, output, false);
        }
    }

    /// <summary>
    /// 命令行入口（可选）
    /// </summary>
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: BinaryToTextEncoder <encode|decode> <inputFile> <outputFile>");
                return;
            }

            string command = args[0].ToLower();
            string input = args[1];
            string output = args[2];

            try
            {
                if (command == "encode")
                {
                    BinaryTextCodec.EncodeFile(input, output);
                    Console.WriteLine("Encoding completed.");
                }
                else if (command == "decode")
                {
                    BinaryTextCodec.DecodeFile(input, output);
                    Console.WriteLine("Decoding completed.");
                }
                else
                {
                    Console.WriteLine("Invalid command. Use 'encode' or 'decode'.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}