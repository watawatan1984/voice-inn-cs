using System;
using System.IO;

namespace VoiceIn.Core;

public static class EnvLoader
{
    public static void Load(string? customPath = null)
    {
        string[] candidatePaths = customPath != null 
            ? [customPath]
            : [
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env"),
                Path.Combine(Directory.GetCurrentDirectory(), ".env"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceIn", ".env")
            ];

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                LoadFile(path);
                return;
            }
        }
    }

    private static void LoadFile(string filePath)
    {
        foreach (var rawLine in File.ReadAllLines(filePath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            int eqIndex = line.IndexOf('=');
            if (eqIndex <= 0)
            {
                continue;
            }

            string key = line[..eqIndex].Trim();
            string val = line[(eqIndex + 1)..].Trim();

            // クォーテーション除去
            if ((val.StartsWith('"') && val.EndsWith('"')) || (val.StartsWith('\'') && val.EndsWith('\'')))
            {
                if (val.Length >= 2)
                {
                    val = val[1..^1];
                }
            }

            Environment.SetEnvironmentVariable(key, val);
        }
    }
}
