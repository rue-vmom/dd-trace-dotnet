//------------------------------------------------------------------------------
// <auto-generated />
// This file was automatically generated by the UpdateVendors tool.
//------------------------------------------------------------------------------
#pragma warning disable CS0618, CS0649, CS1574, CS1580, CS1581, CS1584, CS1591, CS1573, CS8018, SYSLIB0011, SYSLIB0032
#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8620, CS8714, CS8762, CS8765, CS8766, CS8767, CS8768, CS8769, CS8612, CS8629, CS8774
// Decompiled with JetBrains decompiler
// Type: System.Reflection.Internal.ByteArrayMemoryBlock
// Assembly: System.Reflection.Metadata, Version=7.0.0.2, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
// MVID: 2EB35F4B-CF50-496F-AFB8-CC6F6F79CB72

using Datadog.Trace.VendoredMicrosoftCode.System.Collections.Immutable;

#nullable enable
namespace Datadog.Trace.VendoredMicrosoftCode.System.Reflection.Internal
{
  /// <summary>
  /// Represents a memory block backed by an array of bytes.
  /// </summary>
  internal sealed class ByteArrayMemoryBlock : AbstractMemoryBlock
  {

    #nullable disable
    private ByteArrayMemoryProvider _provider;
    private readonly int _start;
    private readonly int _size;


    #nullable enable
    internal ByteArrayMemoryBlock(ByteArrayMemoryProvider provider, int start, int size)
    {
      this._provider = provider;
      this._size = size;
      this._start = start;
    }

    public override void Dispose() => this._provider = (ByteArrayMemoryProvider) null;

    public override unsafe byte* Pointer => this._provider.Pointer + this._start;

    public override int Size => this._size;

    public override ImmutableArray<byte> GetContentUnchecked(int start, int length) => ImmutableArray.Create<byte>(this._provider.Array, this._start + start, length);
  }
}
