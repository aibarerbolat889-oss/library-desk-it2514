#include <stdint.h>

__declspec(dllexport) int32_t __cdecl calculate_fine(int32_t days, int32_t rate, int32_t cap)
{
    if (days < 0 || rate < 0 || cap < 0)
        return -1;

    int64_t total = (int64_t)days * rate;
    return total < cap ? (int32_t)total : cap;
}
