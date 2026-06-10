using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Stream input = Console.OpenStandardInput();
using Stream output = Console.OpenStandardOutput();

while (true)
{
    try
    {
        byte[] lengthBytes = new byte[4];
        int read = ReadExact(input, lengthBytes, 0, 4);

        if (read == 0)
            break;

        int length = BitConverter.ToInt32(lengthBytes, 0);

        byte[] buffer = new byte[length];
        ReadExact(input, buffer, 0, length);

        string json = Encoding.UTF8.GetString(buffer);
        using JsonDocument doc = JsonDocument.Parse(json);

        string action = GetString(doc, "action");

        if (action == "writeAndOpenFile")
        {
            string filePath = GetSafePocPath(GetString(doc, "filePath"));
            string content = GetString(doc, "content");

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, content, Encoding.UTF8);

            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });

            WriteResponse(output, new
            {
                ok = true,
                action,
                filePath
            });
        }
        else if (action == "readFile")
        {
            string filePath = GetSafePocPath(GetString(doc, "filePath"));
            string content = ReadTextFileWhenReady(filePath);

            WriteResponse(output, new
            {
                ok = true,
                action,
                filePath,
                content
            });
        }
        else if (action == "watchFileOnce")
        {
            string filePath = GetSafePocPath(GetString(doc, "filePath"));
            string content = await WatchFileOnceAsync(filePath);

            WriteResponse(output, new
            {
                ok = true,
                action,
                filePath,
                content
            });
        }
        else
        {
            WriteResponse(output, new
            {
                ok = false,
                error = "Unknown action"
            });
        }
    }
    catch (Exception ex)
    {
        WriteResponse(output, new
        {
            ok = false,
            error = ex.Message
        });
    }
}

static string GetString(JsonDocument doc, string propertyName)
{
    if (!doc.RootElement.TryGetProperty(propertyName, out JsonElement value))
        return "";

    return value.GetString() ?? "";
}

static string GetSafePocPath(string requestedPath)
{
    string baseFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "STARLIMS_POC"
    );

    Directory.CreateDirectory(baseFolder);

    if (string.IsNullOrWhiteSpace(requestedPath))
        return Path.Combine(baseFolder, "test-from-browser.txt");

    string fullPath = Path.GetFullPath(requestedPath);

    // POC safety: only allow files inside %USERPROFILE%\STARLIMS_POC
    if (!fullPath.StartsWith(baseFolder, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Path is outside allowed POC folder: {baseFolder}");

    return fullPath;
}

static async Task<string> WatchFileOnceAsync(string filePath)
{
    string directory = Path.GetDirectoryName(filePath)
        ?? throw new InvalidOperationException("Invalid directory.");

    string fileName = Path.GetFileName(filePath);

    TaskCompletionSource<bool> changedSignal = new();

    using FileSystemWatcher watcher = new(directory)
    {
        Filter = fileName,
        NotifyFilter = NotifyFilters.LastWrite
                     | NotifyFilters.Size
                     | NotifyFilters.FileName
                     | NotifyFilters.CreationTime
    };

    FileSystemEventHandler handler = (_, _) => changedSignal.TrySetResult(true);
    RenamedEventHandler renamedHandler = (_, _) => changedSignal.TrySetResult(true);

    watcher.Changed += handler;
    watcher.Created += handler;
    watcher.Renamed += renamedHandler;
    watcher.EnableRaisingEvents = true;

    using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));
    await using (timeout.Token.Register(() => changedSignal.TrySetCanceled()))
    {
        await changedSignal.Task;
    }

    await WaitUntilFileIsStableAsync(filePath);

    return ReadTextFileWhenReady(filePath);
}

static string ReadTextFileWhenReady(string filePath)
{
    WaitUntilFileIsStableAsync(filePath).GetAwaiter().GetResult();
    return File.ReadAllText(filePath, Encoding.UTF8);
}

static async Task WaitUntilFileIsStableAsync(string filePath)
{
    long lastSize = -1;
    int stableCount = 0;

    while (stableCount < 3)
    {
        await Task.Delay(500);

        if (!File.Exists(filePath))
            continue;

        FileInfo info = new(filePath);

        if (info.Length == lastSize && CanOpenExclusive(filePath))
            stableCount++;
        else
            stableCount = 0;

        lastSize = info.Length;
    }
}

static bool CanOpenExclusive(string filePath)
{
    try
    {
        using FileStream stream = File.Open(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None
        );

        return true;
    }
    catch
    {
        return false;
    }
}

static int ReadExact(Stream stream, byte[] buffer, int offset, int count)
{
    int totalRead = 0;

    while (totalRead < count)
    {
        int read = stream.Read(buffer, offset + totalRead, count - totalRead);

        if (read == 0)
            return totalRead;

        totalRead += read;
    }

    return totalRead;
}

static void WriteResponse(Stream output, object response)
{
    string json = JsonSerializer.Serialize(response);
    byte[] bytes = Encoding.UTF8.GetBytes(json);
    byte[] length = BitConverter.GetBytes(bytes.Length);

    output.Write(length, 0, length.Length);
    output.Write(bytes, 0, bytes.Length);
    output.Flush();
}