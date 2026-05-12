#define _GNU_SOURCE
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <unistd.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <linux/perf_event.h>

#if defined(_MSC_VER)
  #include <intrin.h>
  #pragma intrinsic(__rdpmc)
#endif

uintptr_t is_available(void)
{
    return (uintptr_t)1;
}

static inline __attribute__((always_inline))  uintptr_t read_pmc(uint32_t ecx)
{
#if defined(_MSC_VER)
    return (uintptr_t)__rdpmc(ecx);
#elif defined(__i386__) || defined(__x86_64__)
    uint32_t lo, hi;
    __asm__ volatile("rdpmc" : "=a"(lo), "=d"(hi) : "c"(ecx));
    return ((uintptr_t)hi << 32) | lo;
#else
    #error "RDPMC is only supported on x86/x64"
#endif
}

uintptr_t read_instructions_retired(void)
{
    return read_pmc(0x40000000u);
}

uintptr_t read_core_cycles(void)
{
    return read_pmc(0x40000001u);
}

uintptr_t read_core_cycles_lfence(void)
{
#if defined(__i386__) || defined(__x86_64__)
    __asm__ volatile("lfence" ::: "memory");
#endif
    return read_core_cycles();
}

uintptr_t read_core_cycles_lfence_both(void)
{
#if defined(__i386__) || defined(__x86_64__)
    __asm__ volatile("lfence" ::: "memory");
#endif
    uintptr_t value = read_core_cycles();
#if defined(__i386__) || defined(__x86_64__)
    __asm__ volatile("lfence" ::: "memory");
#endif
    return value;
}

uintptr_t read_reference_cycles(void)
{
    return read_pmc(0x40000002u);
}

#if defined(_MSC_VER)
  #pragma intrinsic(__rdtsc)
  #pragma intrinsic(__rdtscp)
#endif

static inline __attribute__((always_inline)) uintptr_t read_tsc(void)
{
#if defined(_MSC_VER)
    return (uintptr_t)__rdtsc();
#elif defined(__i386__) || defined(__x86_64__)
    uint32_t lo, hi;
    __asm__ volatile("rdtsc" : "=a"(lo), "=d"(hi));
    return ((uintptr_t)hi << 32) | lo;
#else
    #error "RDTSC is only supported on x86/x64"
#endif
}

static inline __attribute__((always_inline)) uintptr_t read_tscp(void)
{
#if defined(_MSC_VER)
    unsigned int aux;
    return (uintptr_t)__rdtscp(&aux);
#elif defined(__i386__) || defined(__x86_64__)
    uint32_t lo, hi, aux;
    __asm__ volatile("rdtscp" : "=a"(lo), "=d"(hi), "=c"(aux));
    (void)aux;
    return ((uintptr_t)hi << 32) | lo;
#else
    #error "RDTSCP is only supported on x86/x64"
#endif
}

uintptr_t read_rdtsc(void)
{
    return read_tsc();
}

uintptr_t read_rdtscp(void)
{
    return read_tscp();
}

// Modified from https://github.com/icl-utk-edu/papi/blob/7294c1a6b9793fead3a60805a9ab188a9af66445/src/components/perf_event/perf_helpers.h#L244
static inline __attribute__((always_inline)) int read_perf_programmable_counter(const struct perf_event_mmap_page* pc, uint64_t* counter_value)
{
    uint32_t seq, index, width, time_mult = 0, time_shift = 0;
    int64_t count;
    uint64_t enabled, running;
    uint64_t cyc = 0, time_offset = 0; //, time_cycles = 0, time_mask = ~0ULL;
    int64_t pmc = 0;

     /* In Picollo the fast path is called only when all events report cap_user_rdpmc support */
     /* Not having it here indicates misuse */
    if (!pc->cap_user_rdpmc)
        return -1;

    do {
        /* The kernel increments pc->lock any time */
        /* perf_event_update_userpage() is called */
        /* So by checking now, and the end, we */
        /* can see if an update happened while we */
        /* were trying to read things, and re-try */
        /* if something changed */
        /* The barrier ensures we get the most up to date */
        /* version of the pc->lock variable */

        seq = pc->lock;
        __asm__ volatile("" ::: "memory");

        /* For multiplexing */
        /* time_enabled is time the event was enabled */
        enabled = pc->time_enabled;
        /* time_running is time the event was actually running */
        running = pc->time_running;

        /* if cap_user_time is set, we can use rdtsc */
        /* to calculate more exact enabled/running time */
        /* for more accurate multiplex calculations */
        if ((enabled != running) && pc->cap_user_time) {
            cyc = read_rdtsc();
            time_offset = pc->time_offset;
            time_mult = pc->time_mult;
            time_shift = pc->time_shift;
        }
        
        /* Index of register to read */
        /* 0 means stopped/not-active */
        /* Need to subtract 1 to get actual index to rdpmc() */
        index = pc->index;

        /* Count is the value of the counter the last time the kernel read it */
        width = pc->pmc_width;
        count = pc->offset;

        /* Only read if event index valid */
        /* Otherwise return the older count value */
        if (index) {

            /* Read counter value */
            // __asm__ volatile("lfence" ::: "memory");
            pmc = (int64_t)read_pmc(index - 1u);
            // __asm__ volatile("lfence" ::: "memory");
            /* sign extend result */
            pmc <<= (64u - width);
            pmc >>= (64u - width);

            /* add current count into the existing kernel count */
            count += pmc;
        }

        __asm__ volatile("" ::: "memory");

    } while (pc->lock != seq);

    if (enabled != running)
    {
	    uint64_t quot = (cyc >> time_shift);
        uint64_t rem = cyc & (((uint64_t)1 << time_shift) - 1);
        uint64_t delta = time_offset + quot * time_mult + ((rem * time_mult) >> time_shift);
        
        enabled += delta;
        if (index)
            running += delta;
    }
    
    counter_value[0] = (uint64_t)count;
    counter_value[1] = enabled;
    counter_value[2] = running;

    return 0;
}

int read_perf_programmable_counters(const struct perf_event_mmap_page* const* pcs,
                                    uint64_t* counter_values,
                                    uintptr_t length)
{
    uintptr_t i;

    for (i = 0; i < length; i++)
    {
        int rc = read_perf_programmable_counter(pcs[i], counter_values + (i * 3u));
        if (rc != 0)
        {
            return rc;
        }
    }

    return 0;
}


/*

TODO With Hyper-threading disabled, Intel often gives 8 counters and not just 4 

// cpuid_pmu.c
#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>

typedef struct {
    bool available;

    uint32_t version;
    uint32_t programmable_counters;
    uint32_t programmable_width;
    uint32_t ebx_vector_length;

    uint32_t unavailable_events_ebx;

    uint32_t fixed_counters;
    uint32_t fixed_width;
} CpuPmuInfo;

static inline void cpuid_count(
    uint32_t leaf,
    uint32_t subleaf,
    uint32_t *eax,
    uint32_t *ebx,
    uint32_t *ecx,
    uint32_t *edx)
{
#if defined(__i386__) && defined(__PIC__)
    __asm__ volatile(
        "xchgl %%ebx, %1\n\t"
        "cpuid\n\t"
        "xchgl %%ebx, %1"
        : "=a"(*eax), "=&r"(*ebx), "=c"(*ecx), "=d"(*edx)
        : "0"(leaf), "2"(subleaf)
        : "cc");
#elif defined(__x86_64__) && defined(__PIC__)
    uint64_t rbx64;
    __asm__ volatile(
        "xchgq %%rbx, %q1\n\t"
        "cpuid\n\t"
        "xchgq %%rbx, %q1"
        : "=a"(*eax), "=&r"(rbx64), "=c"(*ecx), "=d"(*edx)
        : "0"(leaf), "2"(subleaf)
        : "cc");
    *ebx = (uint32_t)rbx64;
#else
    __asm__ volatile(
        "cpuid"
        : "=a"(*eax), "=b"(*ebx), "=c"(*ecx), "=d"(*edx)
        : "0"(leaf), "2"(subleaf)
        : "cc");
#endif
}

static bool cpuid_leaf_exists(uint32_t leaf)
{
    uint32_t eax, ebx, ecx, edx;
    cpuid_count(0, 0, &eax, &ebx, &ecx, &edx);
    return eax >= leaf;
}

static CpuPmuInfo read_cpu_pmu_info(void)
{
    CpuPmuInfo info = {0};

#if !(defined(__i386__) || defined(__x86_64__))
    return info;
#else
    if (!cpuid_leaf_exists(0x0A)) {
        return info;
    }

    uint32_t eax, ebx, ecx, edx;
    cpuid_count(0x0A, 0, &eax, &ebx, &ecx, &edx);

    info.available = true;

    info.version                =  eax        & 0xffu;
    info.programmable_counters  = (eax >> 8)  & 0xffu;
    info.programmable_width     = (eax >> 16) & 0xffu;
    info.ebx_vector_length      = (eax >> 24) & 0xffu;

    info.unavailable_events_ebx = ebx;

    info.fixed_counters         =  edx        & 0x1fu;
    info.fixed_width            = (edx >> 5)  & 0xffu;

    return info;
#endif
}

int main(void)
{
    CpuPmuInfo pmu = read_cpu_pmu_info();

    if (!pmu.available) {
        printf("CPUID leaf 0x0A is not available; PMU layout is unknown from CPUID\n");
        return 0;
    }

    printf("PMU version:              %u\n", pmu.version);
    printf("Programmable counters:    %u\n", pmu.programmable_counters);
    printf("Programmable width:       %u\n", pmu.programmable_width);
    printf("EBX vector length:        %u\n", pmu.ebx_vector_length);
    printf("Unavailable events EBX:   0x%08x\n", pmu.unavailable_events_ebx);
    printf("Fixed counters:           %u\n", pmu.fixed_counters);
    printf("Fixed width:              %u\n", pmu.fixed_width);

    return 0;
}


// Without assembly

// cpuid_pmu.c
#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>

#if defined(__i386__) || defined(__x86_64__)
#include <cpuid.h>
#endif

typedef struct {
    bool available;

    uint32_t version;
    uint32_t programmable_counters;
    uint32_t programmable_width;
    uint32_t ebx_vector_length;

    uint32_t unavailable_events_ebx;

    uint32_t fixed_counters;
    uint32_t fixed_width;
} CpuPmuInfo;

static CpuPmuInfo read_cpu_pmu_info(void)
{
    CpuPmuInfo info = {0};

#if defined(__i386__) || defined(__x86_64__)
    unsigned eax, ebx, ecx, edx;

    unsigned max_leaf = __get_cpuid_max(0, NULL);
    if (max_leaf < 0x0A) {
        return info;
    }

    if (!__get_cpuid_count(0x0A, 0, &eax, &ebx, &ecx, &edx)) {
        return info;
    }

    info.available = true;

    info.version               =  eax        & 0xffu;
    info.programmable_counters = (eax >> 8)  & 0xffu;
    info.programmable_width    = (eax >> 16) & 0xffu;
    info.ebx_vector_length     = (eax >> 24) & 0xffu;

    info.unavailable_events_ebx = ebx;

    info.fixed_counters        =  edx        & 0x1fu;
    info.fixed_width           = (edx >> 5)  & 0xffu;
#endif

    return info;
}

int main(void)
{
    CpuPmuInfo pmu = read_cpu_pmu_info();

    if (!pmu.available) {
        printf("CPUID leaf 0x0A is not available; PMU layout is unknown from CPUID\n");
        return 0;
    }

    printf("PMU version:              %u\n", pmu.version);
    printf("Programmable counters:    %u\n", pmu.programmable_counters);
    printf("Programmable width:       %u\n", pmu.programmable_width);
    printf("EBX vector length:        %u\n", pmu.ebx_vector_length);
    printf("Unavailable events EBX:   0x%08x\n", pmu.unavailable_events_ebx);
    printf("Fixed counters:           %u\n", pmu.fixed_counters);
    printf("Fixed width:              %u\n", pmu.fixed_width);

    return 0;
}

*/