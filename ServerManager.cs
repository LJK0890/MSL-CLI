using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace MSL_CLI;

internal class ServerManager
{
    ulong? UUID { get; set; }
    public string? Name { get; private set; }


}
