namespace MNX;

/// <summary>A contiguous run of logical file chunks at consecutive archive positions.</summary>
public readonly record struct ChunkExtent(uint FirstSequence, uint FirstChunk, uint Count);

/// <summary>Adjacent chunks with equal indexed byte lengths. Continuous compression indexes compressed bytes.</summary>
public readonly record struct ChunkLengthGroup(uint FirstSequence, uint Count, uint BytesPerChunk, ulong FirstByteOffset);
