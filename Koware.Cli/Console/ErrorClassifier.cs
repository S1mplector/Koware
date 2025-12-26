// Author: Ilgaz Mehmetoglu
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Koware.Cli.Console;

public enum FriendlyErrorKind
{
    Cancelled,
    Timeout,
    Network,
    Permission,
    NotFound,
    Config,
    ExternalTool,
    Unknown
}

public sealed record FriendlyError(
    FriendlyErrorKind Kind,
    string Title,
    string? Detail = null,
    string? Hint = null);

public static class ErrorClassifier
{
    private static readonly Regex HttpStatusRegex =
        new(@"\b(?:http|status)\s*(?:code\s*)?(\d{3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static FriendlyError Analyze(Exception exception, string? context = null, string? hint = null)
    {
        var ex = Unwrap(exception);

        if (ex is OperationCanceledException)
        {
            return new FriendlyError(FriendlyErrorKind.Cancelled, "Operation cancelled");
        }

        if (ex is TaskCanceledException || ex is TimeoutException)
        {
            var op = string.IsNullOrWhiteSpace(context) ? "operation" : context;
            return new FriendlyError(FriendlyErrorKind.Timeout, $"The {op} timed out");
        }

        if (ex is HttpRequestException || ex is SocketException)
        {
            return new FriendlyError(FriendlyErrorKind.Network, "Network error", SafeDetail(ex));
        }

        if (ex is UnauthorizedAccessException)
        {
            return new FriendlyError(FriendlyErrorKind.Permission, "Access denied", hint: "Check file permissions for the Koware config/data directory.");
        }

        if (ex is FileNotFoundException || ex is DirectoryNotFoundException)
        {
            return new FriendlyError(FriendlyErrorKind.NotFound, "File or directory not found", hint: "Verify the path or reinstall missing components.");
        }

        if (ex is JsonException || ex is FormatException)
        {
            return new FriendlyError(FriendlyErrorKind.Config, "Invalid configuration", hint: "Check appsettings.user.json for invalid values.");
        }

        if (ex is Win32Exception)
        {
            return new FriendlyError(FriendlyErrorKind.ExternalTool, "Failed to launch external tool", hint: "Verify the configured command exists and is executable.");
        }

        var fallbackTitle = string.IsNullOrWhiteSpace(context) ? "Something went wrong" : $"Failed to {context}";
        return new FriendlyError(FriendlyErrorKind.Unknown, fallbackTitle, SafeDetail(ex), hint);
    }

    public static string? SafeDetail(Exception exception)
    {
        var ex = Unwrap(exception);

        switch (ex)
        {
            case HttpRequestException httpEx when httpEx.StatusCode.HasValue:
                return $"HTTP {(int)httpEx.StatusCode.Value}";
            case HttpRequestException httpEx when httpEx.InnerException is SocketException socketEx:
                return SafeDetail(socketEx);
            case HttpRequestException:
                return null;
            case SocketException socketEx:
                return socketEx.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData => "DNS lookup failed",
                    SocketError.ConnectionRefused => "Connection refused",
                    SocketError.TimedOut => "Timed out",
                    _ => "Network error"
                };
            case TimeoutException:
            case TaskCanceledException:
                return "Timed out";
            case OperationCanceledException:
                return "Cancelled";
            case UnauthorizedAccessException:
                return "Access denied";
            case FileNotFoundException:
                return "File not found";
            case DirectoryNotFoundException:
                return "Path not found";
            case JsonException:
                return "Invalid JSON";
            case FormatException:
                return "Invalid format";
            case IOException:
                return "I/O error";
            case Win32Exception:
                return "Failed to start process";
        }

        return SafeDetail(ex.Message);
    }

    public static string? SafeDetail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        var lower = text.ToLowerInvariant();

        var match = HttpStatusRegex.Match(text);
        if (match.Success)
        {
            return $"HTTP {match.Groups[1].Value}";
        }

        if (lower.Contains("ssl") || lower.Contains("certificate"))
        {
            return "Certificate error";
        }

        if (lower.Contains("timeout") || lower.Contains("timed out"))
        {
            return "Timed out";
        }

        if (lower.Contains("unauthorized") || lower.Contains("forbidden") || lower.Contains("access denied") || lower.Contains("permission"))
        {
            return "Access denied";
        }

        if (lower.Contains("not found") || lower.Contains("no such file") || lower.Contains("cannot find"))
        {
            return "Not found";
        }

        if (lower.Contains("name or service not known") || lower.Contains("dns") || lower.Contains("no such host"))
        {
            return "DNS lookup failed";
        }

        if (lower.Contains("connection refused") || lower.Contains("connection reset") || lower.Contains("network") || lower.Contains("socket"))
        {
            return "Network error";
        }

        if (lower.Contains("json"))
        {
            return "Invalid JSON";
        }

        if (lower.Contains("config"))
        {
            return "Invalid configuration";
        }

        if (lower.Contains("cancel"))
        {
            return "Cancelled";
        }

        return null;
    }

    private static Exception Unwrap(Exception ex)
    {
        if (ex is AggregateException agg && agg.InnerExceptions.Count == 1)
        {
            return Unwrap(agg.InnerExceptions[0]);
        }

        if ((ex is TargetInvocationException || ex is TypeInitializationException) && ex.InnerException is not null)
        {
            return Unwrap(ex.InnerException);
        }

        return ex;
    }
}
