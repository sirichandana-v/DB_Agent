namespace MySqlMcpServer.Logging;

public sealed class DailyFileLogger : IDisposable
{
    private readonly string _directory;
    private readonly string _filePrefix;
    private readonly object _fileLock = new();
    private string? _currentDate;
    private StreamWriter? _writer;

    public DailyFileLogger(string directory, string filePrefix)
    {
        _directory = directory;
        _filePrefix = filePrefix;
    }

    public void Log(string level, string message)
    {
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z] [{level}] {message}";
        lock (_fileLock)
        {
            var today = DateTime.UtcNow.ToString("yyyyMMdd");
            if (_currentDate != today || _writer is null)
            {
                _writer?.Dispose();
                _currentDate = today;
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"{_filePrefix}-{today}.log");
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true
                };
            }

            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_fileLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
