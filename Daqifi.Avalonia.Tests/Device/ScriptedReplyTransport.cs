using System.Text;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Google.Protobuf;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// An <see cref="IStreamTransport"/> that answers each command line written to it, so a test can
/// run a wrapper's real <c>Connect()</c> — Core's real <c>InitializeAsync</c> included — with no
/// board.
/// </summary>
/// <remarks>
/// <see cref="CapturingTransport"/> never answers. That is right for a test that only reads the
/// wire, and it is why no test reached <c>InitializeAsync</c> over a transport before #669. Here the
/// reply function gets each command as the device sees it (one line, trimmed) and returns the bytes
/// the device sends back, or <c>null</c> for no answer. <see cref="Board"/> scripts the connect
/// conversation of a board. The transport is its own stream: a read waits for an answer, and
/// reports end-of-stream only after disposal.
/// </remarks>
internal sealed class ScriptedReplyTransport(Func<string, byte[]?> reply) : Stream, IStreamTransport
{
    private readonly object _gate = new();
    private readonly StringBuilder _line = new();
    private readonly List<string> _commands = [];
    private readonly Queue<byte> _answers = new();
    private bool _closed;

    /// <summary>
    /// A board that has an empty error queue and answers a status request with
    /// <paramref name="status"/>, or does not answer it when <paramref name="status"/> is null.
    /// </summary>
    public static ScriptedReplyTransport Board(DaqifiOutMessage? status)
    {
        var errorQuery = ScpiMessageProducer.GetSystemError.Data.Trim();
        var statusQuery = ScpiMessageProducer.GetDeviceInfo.Data.Trim();
        return new ScriptedReplyTransport(command =>
            command == errorQuery ? Encoding.ASCII.GetBytes("0,\"No error\"\r\n")
            : command == statusQuery && status != null ? Delimited(status)
            : null);
    }

    /// <summary>Every command line written so far, in order.</summary>
    public IReadOnlyList<string> Commands
    {
        get { lock (_gate) { return _commands.ToArray(); } }
    }

    public Stream Stream => this;

    public bool IsConnected { get; private set; }

    public string ConnectionInfo => "scripted-reply-transport";

    public event EventHandler<TransportStatusEventArgs>? StatusChanged;

    public Task ConnectAsync() => ConnectAsync(null);

    public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
    {
        Connect();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        Disconnect();
        return Task.CompletedTask;
    }

    public void Connect() => SetConnected(true);

    public void Disconnect() => SetConnected(false);

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            while (_answers.Count == 0 && !_closed)
            {
                Monitor.Wait(_gate);
            }

            var read = 0;
            while (read < count && _answers.Count > 0)
            {
                buffer[offset + read++] = _answers.Dequeue();
            }

            return read;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            foreach (var b in buffer.AsSpan(offset, count))
            {
                if (b != (byte)'\n')
                {
                    _line.Append((char)b);
                    continue;
                }

                var command = _line.ToString().Trim();
                _line.Clear();
                _commands.Add(command);
                foreach (var answer in reply(command) ?? [])
                {
                    _answers.Enqueue(answer);
                }

                Monitor.PulseAll(_gate);
            }
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Disconnect();
            lock (_gate)
            {
                _closed = true;
                Monitor.PulseAll(_gate);
            }
        }

        base.Dispose(disposing);
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        StatusChanged?.Invoke(this, new TransportStatusEventArgs(connected, ConnectionInfo));
    }

    /// <summary>A protobuf message with its length prefix, as the firmware frames it.</summary>
    private static byte[] Delimited(IMessage message)
    {
        using var buffer = new MemoryStream();
        message.WriteDelimitedTo(buffer);
        return buffer.ToArray();
    }
}
