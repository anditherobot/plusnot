namespace plusnot.Pipeline;

public struct PipelineDebugData
{
    public byte[]? RawMask;
    public byte[]? PostMask;
    public byte[]? DiffMask;
    public byte[]? FinalMask;
    public int RawW, RawH;
    public int FinalW, FinalH;
}
