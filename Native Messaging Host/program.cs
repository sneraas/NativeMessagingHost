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

        string action = doc.RootElement.GetProperty("action").GetString() ?? "";
        string fileName = doc.RootElement.GetProperty("fileName").GetString() ?? "test.txt";
        string content = doc.RootElement.GetProperty("content").GetString() ?? "";

        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "STARLIMS_POC"
        );

        Directory.CreateDirectory(folder);

        string safeFileName = Path.GetFileName(fileName);
        string path = Path.Combine(folder, safeFileName);

        if (action == "writeTestFile")
        {
            File.WriteAllText(path, content);

            WriteResponse(output, new
            {
                ok = true,
                path
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