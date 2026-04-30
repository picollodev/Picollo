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

static inline uintptr_t read_pmc(uint32_t ecx)
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

void read_fixed_counters(uintptr_t* instructions_retired, uintptr_t* core_cycles, uintptr_t* reference_cycles)
{
    *instructions_retired = read_instructions_retired();
    *core_cycles = read_core_cycles();
    *reference_cycles = read_reference_cycles();
}

void read_core_cycles_and_instructions(uintptr_t* instructions_retired,
                    uintptr_t* core_cycles)
{
    *instructions_retired = read_instructions_retired();
    *core_cycles = read_core_cycles();
}

#if defined(_MSC_VER)
  #pragma intrinsic(__rdtsc)
  #pragma intrinsic(__rdtscp)
#endif

static inline uintptr_t read_tsc(void)
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

static inline uintptr_t read_tscp(void)
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

void has_cap_user(int32_t* has_user_time, int32_t* has_user_rdpmc)
{
    struct perf_event_attr attr;
    int fd;
    void* page;

    if (has_user_time != NULL)
    {
        *has_user_time = 0;
    }
    if (has_user_rdpmc != NULL)
    {
        *has_user_rdpmc = 0;
    }

    memset(&attr, 0, sizeof(attr));
    attr.size = sizeof(attr);
    attr.type = PERF_TYPE_HARDWARE;
    attr.config = PERF_COUNT_HW_CPU_CYCLES;
    attr.disabled = 1;
    attr.exclude_kernel = 1;
    attr.exclude_hv = 1;
    attr.pinned = 1;

    fd = (int)syscall(SYS_perf_event_open, &attr, 0, -1, -1, 0);
    if (fd < 0)
    {
        return;
    }

    page = mmap(NULL, (size_t)sysconf(_SC_PAGESIZE), PROT_READ, MAP_SHARED, fd, 0);
    if (page == MAP_FAILED)
    {
        close(fd);
        return;
    }

    if (has_user_time != NULL)
    {
        *has_user_time = (int32_t)(((const struct perf_event_mmap_page*)page)->cap_user_time ? 1 : 0);
    }
    if (has_user_rdpmc != NULL)
    {
        *has_user_rdpmc = (int32_t)(((const struct perf_event_mmap_page*)page)->cap_user_rdpmc ? 1 : 0);
    }

    munmap(page, (size_t)sysconf(_SC_PAGESIZE));
    close(fd);
}

// https://github.com/icl-utk-edu/papi/blob/7294c1a6b9793fead3a60805a9ab188a9af66445/src/components/perf_event/perf_helpers.h#L244
static inline int read_perf_programmable_counter(const struct perf_event_mmap_page* pc, uint64_t* counter_value)
{
    uint32_t seq, index, width;
    int64_t count;
    uint64_t enabled, running;
    int64_t pmc = 0;

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

        /* actually do the measurement */

        /* Index of register to read */
        /* 0 means stopped/not-active */
        /* Need to subtract 1 to get actual index to rdpmc() */
        index = pc->index;

        /* count is the value of the counter the last time */
        /* the kernel read it */
        /* If we don't sign extend it, we get large negative */
        /* numbers which break if an IOC_RESET is done */
        width = pc->pmc_width;
        count = pc->offset;

        /* Ugh, libpfm4 perf_event.h has cap_usr_rdpmc */
        /* while actual perf_event.h has cap_user_rdpmc */

        /* Only read if rdpmc enabled and event index valid */
        /* Otherwise return the older (out of date?) count value */
        if (pc->cap_user_rdpmc && index) {

            /* Read counter value */
            pmc = (int64_t)read_pmc(index - 1u);

            /* sign extend result */
            pmc <<= (64u - width);
            pmc >>= (64u - width);

            /* add current count into the existing kernel count */
            count += pmc;
        } else {
            return -1;
        }

        __asm__ volatile("" ::: "memory");

    } while (pc->lock != seq);

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
