using MSL_CLI.IO;
using MSL_CLI.Services;
using System;
using System.IO;

namespace MSL_CLI;

public class TestProgram
{
    public static void Main(string[] args)
    {
        using var globalManager = new GlobalManager();
        globalManager.MainLoop();
    }
}