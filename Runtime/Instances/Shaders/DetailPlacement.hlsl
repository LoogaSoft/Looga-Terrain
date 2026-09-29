#ifndef LOOGA_DETAIL_PLACEMENT_INCLUDED
#define LOOGA_DETAIL_PLACEMENT_INCLUDED

// Deterministic detail distribution shared by every backend.
// GeneratedVegetationPageSource.Hash and Random must stay equal to these functions.

uint DetailHash(uint value)
{
    value ^= value >> 16; value *= 0x7feb352du; value ^= value >> 15;
    value *= 0x846ca68bu; return value ^ (value >> 16);
}

float DetailRandom(uint value)
{
    return (DetailHash(value) & 0x00ffffffu) * (1.0 / 16777216.0);
}

#endif
