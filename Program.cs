using System;
using System.Collections.Generic;
using System.Text;
using MSL_CLI.Models;
using MSL_CLI.Services;

namespace MSL_CLI;
public class MSL_CLI
{
    public static void Main(string[] args)
    {
        ServerArgument serverArgument = new ServerArgument("D:\\games\\minecraft\\server\\versions\\CTI");
        serverArgument.PrintArguments();
    }
}
