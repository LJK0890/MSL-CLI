using System;
using System.Collections.Generic;
using System.Text;

using MSL_CLI.Services;

namespace MSL_CLI;
public class MSL_CLI
{
    public static void Main(string[] args)
    {
        using GlobalManager globalManager = new GlobalManager();
        globalManager.PrintConfig();
    }
}
