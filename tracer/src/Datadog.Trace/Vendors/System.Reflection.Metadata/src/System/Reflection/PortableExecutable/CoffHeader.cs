//------------------------------------------------------------------------------
// <auto-generated />
// This file was automatically generated by the UpdateVendors tool.
//------------------------------------------------------------------------------
#pragma warning disable CS0618, CS0649, CS1574, CS1580, CS1581, CS1584, CS1591, CS1573, CS8018, SYSLIB0011, SYSLIB0032
#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8620, CS8714, CS8762, CS8765, CS8766, CS8767, CS8768, CS8769, CS8612, CS8629, CS8774
#nullable enable
// Decompiled with JetBrains decompiler
// Type: System.Reflection.PortableExecutable.CoffHeader
// Assembly: System.Reflection.Metadata, Version=7.0.0.2, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
// MVID: 2EB35F4B-CF50-496F-AFB8-CC6F6F79CB72

namespace Datadog.Trace.VendoredMicrosoftCode.System.Reflection.PortableExecutable
{
  internal sealed class CoffHeader
  {
    internal const int Size = 20;

    /// <summary>The type of target machine.</summary>
    public Machine Machine { get; }

    /// <summary>
    /// The number of sections. This indicates the size of the section table, which immediately follows the headers.
    /// </summary>
    public short NumberOfSections { get; }

    /// <summary>
    /// The low 32 bits of the number of seconds since 00:00 January 1, 1970, that indicates when the file was created.
    /// </summary>
    public int TimeDateStamp { get; }

    /// <summary>
    /// The file pointer to the COFF symbol table, or zero if no COFF symbol table is present.
    /// This value should be zero for a PE image.
    /// </summary>
    public int PointerToSymbolTable { get; }

    /// <summary>
    /// The number of entries in the symbol table. This data can be used to locate the string table,
    /// which immediately follows the symbol table. This value should be zero for a PE image.
    /// </summary>
    public int NumberOfSymbols { get; }

    /// <summary>
    /// The size of the optional header, which is required for executable files but not for object files.
    /// This value should be zero for an object file.
    /// </summary>
    public short SizeOfOptionalHeader { get; }

    /// <summary>The flags that indicate the attributes of the file.</summary>
    public Characteristics Characteristics { get; }

    internal CoffHeader(ref PEBinaryReader reader)
    {
      this.Machine = (Machine) reader.ReadUInt16();
      this.NumberOfSections = reader.ReadInt16();
      this.TimeDateStamp = reader.ReadInt32();
      this.PointerToSymbolTable = reader.ReadInt32();
      this.NumberOfSymbols = reader.ReadInt32();
      this.SizeOfOptionalHeader = reader.ReadInt16();
      this.Characteristics = (Characteristics) reader.ReadUInt16();
    }
  }
}
