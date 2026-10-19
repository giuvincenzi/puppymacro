using System;
using System.IO;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// A JSON object with a name twice ({"X": 1, "X": 2}) inside a call's argument: System.Text.Json reads it into a JsonNode
/// that throws ArgumentException where its properties are first read (ItemFile.Read refuses such a file for that reason).
/// </summary>
public class MacroStorageDuplicatesTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static readonly LibraryComponent ClickSlot =
        Comp("Click slot", new[] { SlotParam() }, Bound(Click(), ("Point", "Slot")));

    private static string MacroText(Guid id) => $$"""
        {
          "Id": "{{id}}",
          "Name": "Twice",
          "Actions": [
            { "Type": "Component", "ComponentId": "{{ClickSlot.Id}}", "Arguments": { "Slot": { "X": 10, "X": 20, "Y": 30 } } }
          ]
        }
        """;
}
