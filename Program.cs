using MSL_CLI.IO;
using MSL_CLI.Models;
using MSL_CLI.Services;
using System;
using System.IO;

namespace MSL_CLI;

public class TestProgram
{
    public static async Task Main(string[] args)
    {
        using var globalManager = new GlobalManager();
        await globalManager.MainLoop();
    }
}