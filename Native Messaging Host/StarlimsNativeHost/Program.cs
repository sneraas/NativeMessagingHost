using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

internal static class Program
{
    private const int MaxIncomingMessageSize = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static async Task Main()
    {
        DebugMessage(
            "1. Native Messaging Host started.",
            "START"
        );

        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();

        try
        {
            DebugMessage(
                "2. Waiting for message from Edge extension.",
                "WAITING"
            );

            string? json = ReadNativeMessage(input);

            if (json is null)
            {
                DebugMessage(
                    "Input stream was closed before any message was received.",
                    "ERROR"
                );

                return;
            }

            DebugMessage(
                $"3. Message received.\n\n" +
                $"Length: {json.Length}\n\n" +
                $"{json}",
                "MESSAGE RECEIVED"
            );

            NativeRequest request =
                JsonSerializer.Deserialize<NativeRequest>(
                    json,
                    JsonOptions
                )
                ?? throw new InvalidOperationException(
                    "Could not deserialize request."
                );

            DebugMessage(
                $"4. JSON parsed successfully.\n\n" +
                $"Token length: {request.Token.Length}\n" +
                $"Key credential: {request.Key}\n" +
                $"Secret credential: {request.Secret}",
                "JSON OK"
            );

            ValidateRequest(request);

            DebugMessage(
                "5. Request validation passed.",
                "REQUEST OK"
            );

            ApiFileResult apiResult =
                await CallStarlimsApiAsync(request);

            DebugMessage(
                $"11. STARLIMS result parsed.\n\n" +
                $"FILE_ACTION: {apiResult.FileAction}\n" +
                $"FILE_NAME: {apiResult.FileName}\n" +
                $"CLIENT_FILE_PATH: {apiResult.ClientFilePath}\n" +
                $"FILE_DATA length: {apiResult.FileData.Length}",
                "API RESULT"
            );

            string filePath =
                HandleApiResult(apiResult);

            DebugMessage(
                $"14. Everything completed successfully.\n\n" +
                $"File:\n{filePath}",
                "SUCCESS"
            );

            WriteResponse(
                output,
                new
                {
                    ok = true,
                    fileAction = apiResult.FileAction,
                    filePath
                }
            );
        }
        catch (Exception ex)
        {
            DebugMessage(
                $"Native host failed.\n\n" +
                $"Type:\n{ex.GetType().FullName}\n\n" +
                $"Message:\n{ex.Message}\n\n" +
                $"Stack:\n{ex.StackTrace}",
                "NATIVE HOST ERROR"
            );

            try
            {
                WriteResponse(
                    output,
                    new
                    {
                        ok = false,
                        error = ex.Message
                    }
                );
            }
            catch
            {
                // Do nothing.
            }
        }
    }

    private static void ValidateRequest(
        NativeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            throw new InvalidOperationException(
                "Token is missing."
            );
        }

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            throw new InvalidOperationException(
                "Key credential name is missing."
            );
        }

        if (string.IsNullOrWhiteSpace(request.Secret))
        {
            throw new InvalidOperationException(
                "Secret credential name is missing."
            );
        }
    }

    private static async Task<ApiFileResult> CallStarlimsApiAsync(
        NativeRequest nativeRequest)
    {
        string environment =
            GetEnvironmentFromCredentialTargets(
                nativeRequest.Key,
                nativeRequest.Secret
            );

        DebugMessage(
            $"6. Credential names validated.\n\n" +
            $"Environment: {environment}\n" +
            $"Key: {nativeRequest.Key}\n" +
            $"Secret: {nativeRequest.Secret}",
            "ENVIRONMENT"
        );

        CredentialEntry keyCredential;

        try
        {
            keyCredential =
                CredentialManager.ReadGeneric(
                    nativeRequest.Key
                );
        }
        catch (Exception ex)
        {
            DebugMessage(
                $"Could not read credential:\n" +
                $"{nativeRequest.Key}\n\n" +
                $"{ex.Message}\n\n" +
                $"IMPORTANT:\n" +
                $"The credential must be stored as a Generic Credential.",
                "KEY CREDENTIAL FAILED"
            );

            throw;
        }

        DebugMessage(
            $"7. API key credential found.\n\n" +
            $"Target: {nativeRequest.Key}\n" +
            $"Username: {keyCredential.UserName}\n" +
            $"Password length: {keyCredential.Password.Length}\n\n" +
            $"Password itself is intentionally not shown.",
            "API KEY FOUND"
        );

        CredentialEntry secretCredential;

        try
        {
            secretCredential =
                CredentialManager.ReadGeneric(
                    nativeRequest.Secret
                );
        }
        catch (Exception ex)
        {
            DebugMessage(
                $"Could not read credential:\n" +
                $"{nativeRequest.Secret}\n\n" +
                $"{ex.Message}\n\n" +
                $"IMPORTANT:\n" +
                $"The credential must be stored as a Generic Credential.",
                "SECRET CREDENTIAL FAILED"
            );

            throw;
        }

        DebugMessage(
            $"8. API secret credential found.\n\n" +
            $"Target: {nativeRequest.Secret}\n" +
            $"Username: {secretCredential.UserName}\n" +
            $"Password length: {secretCredential.Password.Length}\n\n" +
            $"Password itself is intentionally not shown.",
            "API SECRET FOUND"
        );

        if (!string.Equals(
                keyCredential.UserName,
                environment,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"API key username '{keyCredential.UserName}' " +
                $"does not match environment '{environment}'."
            );
        }

        if (!string.Equals(
                secretCredential.UserName,
                environment,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"API secret username '{secretCredential.UserName}' " +
                $"does not match environment '{environment}'."
            );
        }

        if (string.IsNullOrWhiteSpace(
                keyCredential.Password))
        {
            throw new UnauthorizedAccessException(
                "API key credential contains no password."
            );
        }

        if (string.IsNullOrWhiteSpace(
                secretCredential.Password))
        {
            throw new UnauthorizedAccessException(
                "API secret credential contains no password."
            );
        }

        DebugMessage(
            "9. Both credentials are valid and belong " +
            $"to environment '{environment}'.",
            "CREDENTIALS OK"
        );

        string accessKey =
            keyCredential.Password;

        string secretKey =
            secretCredential.Password;

        string apiBaseUrl =
            GetApiUrl(environment);

        string url =
            $"{apiBaseUrl}?token=" +
            Uri.EscapeDataString(
                nativeRequest.Token
            );

        const string method = "GET";
        const string apiVerb = "";
        const string body = "";

        string timestamp =
            DateTime.UtcNow.ToString(
                "yyyy-MM-ddTHH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture
            );

        string stringToSign =
            $"{url}\n" +
            $"{method}\n" +
            $"{accessKey}\n" +
            $"{apiVerb}\n" +
            $"{timestamp}\n" +
            $"{body}";

        string signature =
            CreateSignature(
                stringToSign,
                secretKey
            );

        DebugMessage(
            $"10. API request prepared.\n\n" +
            $"URL:\n{url}\n\n" +
            $"Timestamp:\n{timestamp}\n\n" +
            $"Access key length: {accessKey.Length}\n" +
            $"Secret key length: {secretKey.Length}\n" +
            $"Signature length: {signature.Length}",
            "CALLING STARLIMS"
        );

        using HttpClientHandler handler = new()
        {
            UseDefaultCredentials = true,
            PreAuthenticate = true
        };

        using HttpClient client =
            new(handler);

        using HttpRequestMessage request =
            new(
                HttpMethod.Get,
                url
            );

        request.Headers.TryAddWithoutValidation(
            "SL-API-Auth",
            accessKey
        );

        request.Headers.TryAddWithoutValidation(
            "SL-API-Timestamp",
            timestamp
        );

        request.Headers.TryAddWithoutValidation(
            "SL-API-Signature",
            signature
        );

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/json"
            )
        );

        HttpResponseMessage response;

        try
        {
            response =
                await client.SendAsync(
                    request
                );
        }
        catch (Exception ex)
        {
            DebugMessage(
                $"HTTP request itself failed.\n\n" +
                $"{ex.GetType().Name}\n\n" +
                $"{ex.Message}",
                "HTTP FAILED"
            );

            throw;
        }

        using (response)
        {
            string responseBody =
                await response.Content.ReadAsStringAsync();

            DebugMessage(
                $"STARLIMS responded.\n\n" +
                $"HTTP status: {(int)response.StatusCode}\n" +
                $"{response.StatusCode}\n\n" +
                $"Response length: {responseBody.Length}",
                "HTTP RESPONSE"
            );

            if (!response.IsSuccessStatusCode)
            {
                string preview =
                    responseBody.Length > 2000
                        ? responseBody[..2000]
                        : responseBody;

                DebugMessage(
                    $"STARLIMS API returned an error.\n\n" +
                    $"HTTP {(int)response.StatusCode} " +
                    $"{response.ReasonPhrase}\n\n" +
                    $"Response:\n{preview}",
                    "STARLIMS API ERROR"
                );

                throw new HttpRequestException(
                    $"STARLIMS API returned " +
                    $"{(int)response.StatusCode} " +
                    $"{response.ReasonPhrase}."
                );
            }

            ApiResponse? apiResponse;

            try
            {
                apiResponse =
                    JsonSerializer.Deserialize<ApiResponse>(
                        responseBody,
                        JsonOptions
                    );
            }
            catch (Exception ex)
            {
                DebugMessage(
                    $"Could not parse STARLIMS JSON.\n\n" +
                    $"{ex.Message}\n\n" +
                    $"Response length: {responseBody.Length}",
                    "JSON PARSE FAILED"
                );

                throw;
            }

            if (apiResponse?.Result is null)
            {
                throw new InvalidOperationException(
                    "STARLIMS returned no Result property."
                );
            }

            if (apiResponse.Result.Count == 0)
            {
                throw new InvalidOperationException(
                    "STARLIMS returned an empty Result array."
                );
            }

            return apiResponse.Result[0];
        }
    }

    private static string CreateSignature(
        string stringToSign,
        string secretKey)
    {
        byte[] keyBytes =
            Encoding.UTF8.GetBytes(
                secretKey
            );

        byte[] dataBytes =
            Encoding.UTF8.GetBytes(
                stringToSign
            );

        byte[] hashBytes;

        using (
            HMACSHA256 hmac =
                new(keyBytes)
        )
        {
            hashBytes =
                hmac.ComputeHash(
                    dataBytes
                );
        }

        string signatureRaw =
            Convert.ToBase64String(
                hashBytes
            );

        /*
         * Matches the URL encoding performed
         * by the PowerShell proof of concept.
         */
        return WebUtility.UrlEncode(
            signatureRaw
        );
    }

    private static string GetEnvironmentFromCredentialTargets(
        string keyTarget,
        string secretTarget)
    {
        const string keySuffix =
            "_API_KEY";

        const string secretSuffix =
            "_API_SECRET";

        if (!keyTarget.EndsWith(
                keySuffix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Invalid key credential name: {keyTarget}"
            );
        }

        if (!secretTarget.EndsWith(
                secretSuffix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Invalid secret credential name: {secretTarget}"
            );
        }

        string keyEnvironment =
            keyTarget[
                ..^keySuffix.Length
            ];

        string secretEnvironment =
            secretTarget[
                ..^secretSuffix.Length
            ];

        if (!string.Equals(
                keyEnvironment,
                secretEnvironment,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "API key and secret reference different environments."
            );
        }

        if (string.IsNullOrWhiteSpace(
                keyEnvironment))
        {
            throw new UnauthorizedAccessException(
                "Environment name is empty."
            );
        }

        return keyEnvironment
            .ToUpperInvariant();
    }

    private static string GetApiUrl(
        string environment)
    {
        return environment switch
        {
            "DEV" =>
                "https://rhs-limsapou-83.ad.ous-hf.no/" +
                "STARLIMS.DEV/rest.web.api/v1/" +
                "Folders/SendDocumentToClient",

            /*
             * Add PROD later:
             *
             * "PROD" =>
             *     "https://production-server/" +
             *     "STARLIMS/rest.web.api/v1/" +
             *     "Folders/SendDocumentToClient",
             */

            _ =>
                throw new UnauthorizedAccessException(
                    $"Environment '{environment}' is not configured."
                )
        };
    }

    private static string HandleApiResult(
        ApiFileResult result)
    {
        if (string.IsNullOrWhiteSpace(
                result.FileAction))
        {
            throw new InvalidOperationException(
                "FILE_ACTION is missing."
            );
        }

        DebugMessage(
            $"12. Processing FILE_ACTION:\n\n" +
            $"{result.FileAction}",
            "FILE ACTION"
        );

        switch (
            result.FileAction
                .Trim()
                .ToLowerInvariant()
        )
        {
            case "readwrite":
                return SaveAndOpenFile(
                    result
                );

            default:
                throw new InvalidOperationException(
                    $"Unsupported FILE_ACTION: {result.FileAction}"
                );
        }
    }

    private static string SaveAndOpenFile(
        ApiFileResult result)
    {
        if (string.IsNullOrWhiteSpace(
                result.ClientFilePath))
        {
            throw new InvalidOperationException(
                "CLIENT_FILE_PATH is missing."
            );
        }

        if (string.IsNullOrWhiteSpace(
                result.FileName))
        {
            throw new InvalidOperationException(
                "FILE_NAME is missing."
            );
        }

        if (string.IsNullOrWhiteSpace(
                result.FileData))
        {
            throw new InvalidOperationException(
                "FILE_DATA is missing."
            );
        }

        string safeFileName =
            Path.GetFileName(
                result.FileName
            );

        if (string.IsNullOrWhiteSpace(
                safeFileName))
        {
            throw new InvalidOperationException(
                "FILE_NAME is invalid."
            );
        }

        string directory =
            Path.GetFullPath(
                result.ClientFilePath
            );

        string filePath =
            Path.Combine(
                directory,
                safeFileName
            );

        DebugMessage(
            $"13. Ready to write file.\n\n" +
            $"Directory:\n{directory}\n\n" +
            $"Filename:\n{safeFileName}\n\n" +
            $"Final path:\n{filePath}",
            "WRITING FILE"
        );

        Directory.CreateDirectory(
            directory
        );

        byte[] fileBytes;

        try
        {
            fileBytes =
                Convert.FromBase64String(
                    result.FileData
                );
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "FILE_DATA is not valid Base64.",
                ex
            );
        }

        DebugMessage(
            $"Base64 decoded successfully.\n\n" +
            $"Binary file size: {fileBytes.Length} bytes",
            "BASE64 OK"
        );

        try
        {
            File.WriteAllBytes(
                filePath,
                fileBytes
            );
        }
        catch (Exception ex)
        {
            DebugMessage(
                $"Could not write file.\n\n" +
                $"Path:\n{filePath}\n\n" +
                $"{ex.GetType().Name}\n" +
                $"{ex.Message}",
                "FILE WRITE FAILED"
            );

            throw;
        }

        DebugMessage(
            $"File written successfully.\n\n" +
            $"{filePath}",
            "FILE SAVED"
        );

        try
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                }
            );
        }
        catch (Exception ex)
        {
            DebugMessage(
                $"The file exists, but Windows could not open it.\n\n" +
                $"Path:\n{filePath}\n\n" +
                $"{ex.GetType().Name}\n" +
                $"{ex.Message}",
                "OPEN FILE FAILED"
            );

            throw;
        }

        DebugMessage(
            $"Windows was asked to open:\n\n" +
            $"{filePath}",
            "FILE OPENED"
        );

        return filePath;
    }

    private static string? ReadNativeMessage(
        Stream input)
    {
        byte[] lengthBytes =
            new byte[4];

        int lengthRead =
            ReadExact(
                input,
                lengthBytes,
                0,
                4
            );

        if (lengthRead == 0)
            return null;

        if (lengthRead != 4)
        {
            throw new EndOfStreamException(
                "Native message header was incomplete."
            );
        }

        int length =
            BitConverter.ToInt32(
                lengthBytes,
                0
            );

        DebugMessage(
            $"Native message header received.\n\n" +
            $"Payload length: {length} bytes",
            "NATIVE MESSAGE HEADER"
        );

        if (length <= 0)
        {
            throw new InvalidOperationException(
                $"Invalid message length: {length}"
            );
        }

        if (length > MaxIncomingMessageSize)
        {
            throw new InvalidOperationException(
                $"Message is too large: {length} bytes."
            );
        }

        byte[] buffer =
            new byte[length];

        int bodyRead =
            ReadExact(
                input,
                buffer,
                0,
                length
            );

        if (bodyRead != length)
        {
            throw new EndOfStreamException(
                $"Expected {length} bytes but received {bodyRead}."
            );
        }

        return Encoding.UTF8.GetString(
            buffer
        );
    }

    private static int ReadExact(
        Stream stream,
        byte[] buffer,
        int offset,
        int count)
    {
        int totalRead = 0;

        while (totalRead < count)
        {
            int read =
                stream.Read(
                    buffer,
                    offset + totalRead,
                    count - totalRead
                );

            if (read == 0)
                return totalRead;

            totalRead += read;
        }

        return totalRead;
    }

    private static void WriteResponse(
        Stream output,
        object response)
    {
        string json =
            JsonSerializer.Serialize(
                response
            );

        byte[] bytes =
            Encoding.UTF8.GetBytes(
                json
            );

        byte[] length =
            BitConverter.GetBytes(
                bytes.Length
            );

        output.Write(
            length,
            0,
            length.Length
        );

        output.Write(
            bytes,
            0,
            bytes.Length
        );

        output.Flush();
    }

    private static void DebugMessage(
        string message,
        string title)
    {
        MessageBoxW(
            IntPtr.Zero,
            message,
            $"STARLIMS Native Host - {title}",
            0
        );
    }

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int MessageBoxW(
        IntPtr hWnd,
        string lpText,
        string lpCaption,
        uint uType
    );
}

internal sealed class NativeRequest
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("secret")]
    public string Secret { get; set; } = "";
}

internal sealed class ApiResponse
{
    [JsonPropertyName("Result")]
    public List<ApiFileResult>? Result { get; set; }
}

internal sealed class ApiFileResult
{
    [JsonPropertyName("CLIENT_FILE_PATH")]
    public string ClientFilePath { get; set; } = "";

    [JsonPropertyName("FILE_ACTION")]
    public string FileAction { get; set; } = "";

    [JsonPropertyName("FILE_NAME")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("FILE_DATA")]
    public string FileData { get; set; } = "";
}

internal sealed record CredentialEntry(
    string UserName,
    string Password
);

internal static class CredentialManager
{
    private const uint CredTypeGeneric = 1;

    public static CredentialEntry ReadGeneric(
        string target)
    {
        if (string.IsNullOrWhiteSpace(
                target))
        {
            throw new ArgumentException(
                "Credential target is empty.",
                nameof(target)
            );
        }

        bool success =
            CredReadW(
                target,
                CredTypeGeneric,
                0,
                out IntPtr credentialPointer
            );

        if (!success)
        {
            int error =
                Marshal.GetLastWin32Error();

            throw new Win32Exception(
                error,
                $"Generic credential '{target}' " +
                $"could not be read. " +
                $"Win32 error: {error}"
            );
        }

        try
        {
            NativeCredential credential =
                Marshal.PtrToStructure<NativeCredential>(
                    credentialPointer
                );

            string userName =
                Marshal.PtrToStringUni(
                    credential.UserName
                )
                ?? "";

            string password = "";

            if (
                credential.CredentialBlob != IntPtr.Zero &&
                credential.CredentialBlobSize > 0
            )
            {
                int blobSize =
                    checked(
                        (int)credential.CredentialBlobSize
                    );

                byte[] passwordBytes =
                    new byte[blobSize];

                Marshal.Copy(
                    credential.CredentialBlob,
                    passwordBytes,
                    0,
                    blobSize
                );

                password =
                    Encoding.Unicode
                        .GetString(
                            passwordBytes
                        )
                        .TrimEnd('\0');

                CryptographicOperations.ZeroMemory(
                    passwordBytes
                );
            }

            return new CredentialEntry(
                userName,
                password
            );
        }
        finally
        {
            CredFree(
                credentialPointer
            );
        }
    }

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(
        string target,
        uint type,
        uint flags,
        out IntPtr credential
    );

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredFree",
        SetLastError = true)]
    private static extern void CredFree(
        IntPtr buffer
    );

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;

        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;

        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
