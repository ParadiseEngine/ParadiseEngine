namespace Paradise.Rendering.WebGPU;

/// <summary>Investigation-only CPU wall time, including driver waits, around frame submission.</summary>
public readonly record struct WebGpuSubmissionTimings(double Acquire, double Encode, double Finish, double Queue, double Present, bool Presented);
