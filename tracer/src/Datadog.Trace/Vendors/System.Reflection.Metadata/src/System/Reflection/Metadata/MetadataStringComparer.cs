//------------------------------------------------------------------------------
// <auto-generated />
// This file was automatically generated by the UpdateVendors tool.
//------------------------------------------------------------------------------
#pragma warning disable CS0618, CS0649, CS1574, CS1580, CS1581, CS1584, CS1591, CS1573, CS8018, SYSLIB0011, SYSLIB0032
#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8620, CS8714, CS8762, CS8765, CS8766, CS8767, CS8768, CS8769, CS8612, CS8629, CS8774
// Decompiled with JetBrains decompiler
// Type: System.Reflection.Metadata.MetadataStringComparer
// Assembly: System.Reflection.Metadata, Version=7.0.0.2, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
// MVID: 2EB35F4B-CF50-496F-AFB8-CC6F6F79CB72


#nullable enable
namespace Datadog.Trace.VendoredMicrosoftCode.System.Reflection.Metadata
{
  /// <summary>
  /// Provides string comparison helpers to query strings in metadata while
  /// avoiding allocation where possible.
  /// </summary>
  /// <remarks>
  /// No allocation is performed unless both the handle argument and the
  /// value argument contain non-ascii text.
  /// 
  /// Obtain instances using <see cref="P:System.Reflection.Metadata.MetadataReader.StringComparer" />.
  /// 
  /// A default-initialized instance is useless and behaves as a null reference.
  /// 
  /// The code is optimized such that there is no additional overhead in
  /// re-obtaining a comparer over hoisting it in to a local.
  /// 
  /// That is to say that a construct like:
  /// 
  /// <code>
  /// if (reader.StringComparer.Equals(typeDef.Namespace, "System") &amp;&amp;
  ///     reader.StringComparer.Equals(typeDef.Name, "Object")
  /// {
  ///     // found System.Object
  /// }
  /// </code>
  /// 
  /// is no less efficient than:
  /// 
  /// <code>
  /// var comparer = reader.StringComparer;
  /// if (comparer.Equals(typeDef.Namespace, "System") &amp;&amp;
  ///     comparer.Equals(typeDef.Name, "Object")
  /// {
  ///     // found System.Object
  /// }
  /// </code>
  /// 
  /// The choice between them is therefore one of style and not performance.
  /// </remarks>
  internal readonly struct MetadataStringComparer
  {

    #nullable disable
    private readonly MetadataReader _reader;


    #nullable enable
    internal MetadataStringComparer(MetadataReader reader) => this._reader = reader;

    public bool Equals(StringHandle handle, string value) => this.Equals(handle, value, false);

    public bool Equals(StringHandle handle, string value, bool ignoreCase)
    {
      if (value == null)
        Throw.ValueArgumentNull();
      return this._reader.StringHeap.Equals(handle, value, this._reader.UTF8Decoder, ignoreCase);
    }

    public bool Equals(NamespaceDefinitionHandle handle, string value) => this.Equals(handle, value, false);

    public bool Equals(NamespaceDefinitionHandle handle, string value, bool ignoreCase)
    {
      if (value == null)
        Throw.ValueArgumentNull();
      return handle.HasFullName ? this._reader.StringHeap.Equals(handle.GetFullName(), value, this._reader.UTF8Decoder, ignoreCase) : value == this._reader.NamespaceCache.GetFullName(handle);
    }

    public bool Equals(DocumentNameBlobHandle handle, string value) => this.Equals(handle, value, false);

    public bool Equals(DocumentNameBlobHandle handle, string value, bool ignoreCase)
    {
      if (value == null)
        Throw.ValueArgumentNull();
      return this._reader.BlobHeap.DocumentNameEquals(handle, value, ignoreCase);
    }

    public bool StartsWith(StringHandle handle, string value) => this.StartsWith(handle, value, false);

    public bool StartsWith(StringHandle handle, string value, bool ignoreCase)
    {
      if (value == null)
        Throw.ValueArgumentNull();
      return this._reader.StringHeap.StartsWith(handle, value, this._reader.UTF8Decoder, ignoreCase);
    }
  }
}
