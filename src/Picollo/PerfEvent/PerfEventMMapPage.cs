using System;
using System.Runtime.InteropServices;

namespace Picollo.PerfEvent;

/// <summary>
/// perf_event_mmap_page https://codebrowser.dev/linux/linux/include/uapi/linux/perf_event.h.html#perf_event_mmap_page
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PerfEventMMapPage
{
    /// <summary>
    /// version number of this structure
    /// </summary>
    public uint Version;

    /// <summary>
    /// lowest version this is compat with
    /// </summary>
    public uint CompatVersion;

    /*
     * Bits needed to read the HW events in user-space.
     *
     *   u32 seq, time_mult, time_shift, index, width;
     *   u64 count, enabled, running;
     *   u64 cyc, time_offset;
     *   s64 pmc = 0;
     *
     *   do {
     *     seq = pc->lock;
     *     barrier()
     *
     *     enabled = pc->time_enabled;
     *     running = pc->time_running;
     *
     *     if (pc->cap_usr_time && enabled != running) {
     *       cyc = rdtsc();
     *       time_offset = pc->time_offset;
     *       time_mult   = pc->time_mult;
     *       time_shift  = pc->time_shift;
     *     }
     *
     *     index = pc->index;
     *     count = pc->offset;
     *     if (pc->cap_user_rdpmc && index) {
     *       width = pc->pmc_width;
     *       pmc = rdpmc(index - 1);
     *     }
     *
     *     barrier();
     *   } while (pc->lock != seq);
     *
     * NOTE: for obvious reason this only works on self-monitoring
     *       processes.
     */

    /// <summary>
    /// seqlock for synchronization
    /// </summary>
    public uint Lock;

    /// <summary>
    /// hardware event identifier
    /// </summary>
    public uint Index;

    /// <summary>
    /// add to hardware event value
    /// </summary>
    public long Offset;

    /// <summary>
    /// time event active
    /// </summary>
    public ulong TimeEnabled;

    /// <summary>
    /// time event on CPU
    /// </summary>
    public ulong TimeRunning;

    public PerfEventMmapCapabilities Capabilities;

    /*
     * If cap_user_rdpmc this field provides the bit-width of the value
     * read using the rdpmc() or equivalent instruction. This can be used
     * to sign extend the result like:
     *
     *   pmc <<= 64 - width;
     *   pmc >>= 64 - width; // signed shift right
     *   count += pmc;
     */
    public ushort PMCWidth;

    /*
     * If cap_usr_time the below fields can be used to compute the time
     * delta since time_enabled (in ns) using RDTSC or similar.
     *
     *   u64 quot, rem;
     *   u64 delta;
     *
     *   quot = (cyc >> time_shift);
     *   rem = cyc & (((u64)1 << time_shift) - 1);
     *   delta = time_offset + quot * time_mult +
     *              ((rem * time_mult) >> time_shift);
     *
     * Where time_offset,time_mult,time_shift and cyc are read in the
     * seqcount loop described above. This delta can then be added to
     * enabled and possible running (if index), improving the scaling:
     *
     *   enabled += delta;
     *   if (index)
     *     running += delta;
     *
     *   quot = count / running;
     *   rem  = count % running;
     *   count = quot * enabled + (rem * enabled) / running;
     */
    public ushort TimeShift;

    public uint TimeMult;

    public ulong TimeOffset;

    /*
     * If cap_usr_time_zero, the hardware clock (e.g. TSC) can be calculated
     * from sample timestamps.
     *
     *   time = timestamp - time_zero;
     *   quot = time / time_mult;
     *   rem  = time % time_mult;
     *   cyc = (quot << time_shift) + (rem << time_shift) / time_mult;
     *
     * And vice versa:
     *
     *   quot = cyc >> time_shift;
     *   rem  = cyc & (((u64)1 << time_shift) - 1);
     *   timestamp = time_zero + quot * time_mult +
     *               ((rem * time_mult) >> time_shift);
     */
    public ulong TimeZero;

    /// <summary>
    /// Header size up to __reserved[] fields
    /// </summary>
    public uint Size;

    public uint Reserved1;

    /*
     * If cap_usr_time_short, the hardware clock is less than 64bit wide
     * and we must compute the 'cyc' value, as used by cap_usr_time, as:
     *
     *   cyc = time_cycles + ((cyc - time_cycles) & time_mask)
     *
     * NOTE: this form is explicitly chosen such that cap_usr_time_short
     *       is a correction on top of cap_usr_time, and code that doesn't
     *       know about cap_usr_time_short still works under the assumption
     *       the counter doesn't wrap.
     */
    public ulong TimeCycles;
    public ulong TimeMask;

    public unsafe fixed byte Reserved[116 * 8];

    /*
     * Control data for the mmap() data buffer.
     *
     * User-space reading the @data_head value should issue an smp_rmb(),
     * after reading this value.
     *
     * When the mapping is PROT_WRITE the @data_tail value should be
     * written by user-space to reflect the last read data, after issuing
     * an smp_mb() to separate the data read from the ->data_tail store.
     * In this case the kernel will not over-write unread data.
     *
     * See perf_output_put_handle() for the data ordering.
     *
     * data_{offset,size} indicate the location and size of the perf record
     * buffer within the mmapped area.
     */

    public ulong DataHead;

    public ulong DataTail;

    public ulong DataOffset;

    public ulong DataSize;

    /*
     * AUX area is defined by aux_{offset,size} fields that should be set
     * by the user-space, so that
     *
     *   aux_offset >= data_offset + data_size
     *
     * prior to mmap()ing it. Size of the mmap()ed area should be aux_size.
     *
     * Ring buffer pointers aux_{head,tail} have the same semantics as
     * data_{head,tail} and same ordering rules apply.
     */
    public ulong AUXHead;

    public ulong AUXTail;

    public ulong AUXOffset;

    public ulong AUXSize;
}

[Flags]
public enum PerfEventMmapCapabilities : ulong
{
    None = 0,

    /// <summary>
    /// bit 0 deprecated / always 0 
    /// </summary>
    CapBit0 = 1UL << 0,

    /// <summary>
    /// Always 1, signals that bit 0 is zero
    /// </summary>
    CapBit0IsDeprecated = 1UL << 1,

    /// <summary>
    /// The RDPMC instruction can be used to read counts
    /// </summary>
    CapUserRdpmc = 1UL << 2,

    /// <summary>
    /// The time_{shift,mult,offset} fields are used
    /// </summary>
    CapUserTime = 1UL << 3,

    /// <summary>
    /// The time_zero field is used
    /// </summary>
    CapUserTimeZero = 1UL << 4,

    /// <summary>
    /// the time_{cycle,mask} fields are used
    /// </summary>
    CapUserTimeShort = 1UL << 5,
}