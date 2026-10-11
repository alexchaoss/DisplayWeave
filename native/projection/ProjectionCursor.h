#pragma once
#include <cstdint>
#include <string>
#include <vector>
#include <limits>
#include <utility>
#include <cstddef>

// DDA pointer shapes are small CPU metadata, never desktop pixel readbacks.
// The shader receives exact integer RGBA channels so AND/XOR masks survive.
namespace projection_cursor {
constexpr uint32_t Monochrome = 1, Color = 2, MaskedColor = 4;
constexpr uint32_t MaximumDimension = 1024;
constexpr size_t MaximumBytes = 8 * 1024 * 1024;
struct Shape { uint32_t type = 0, width = 0, height = 0; std::vector<uint8_t> rgba; };
inline int32_t CropOffset(int32_t outputPosition, int32_t cropOffset, int32_t cropExtent) {
    const int64_t value = static_cast<int64_t>(outputPosition) - cropOffset;
    // Fully off-crop pointers stay outside the image. This also bounds shader
    // subtraction even when a driver supplies extreme output-relative values.
    if (value < -static_cast<int64_t>(MaximumDimension)) return -static_cast<int32_t>(MaximumDimension);
    if (value > cropExtent) return cropExtent;
    return static_cast<int32_t>(value);
}
inline bool Decode(uint32_t type, uint32_t width, uint32_t rawHeight, uint32_t pitch,
                   const uint8_t* data, size_t bytes, Shape& output, std::string& error) {
    error.clear();
    const bool mono = type == Monochrome;
    if (type != Monochrome && type != Color && type != MaskedColor) { error = "Unknown Desktop Duplication pointer type."; return false; }
    const uint32_t height = mono ? rawHeight / 2 : rawHeight;
    if (!width || !height || width > MaximumDimension || height > MaximumDimension || (mono && rawHeight % 2)) {
        error = "Invalid or excessive Desktop Duplication pointer dimensions."; return false;
    }
    const uint32_t minimumPitch = mono ? (width + 7) / 8 : width * 4;
    const uint64_t required = static_cast<uint64_t>(pitch) * rawHeight;
    if (!data || pitch < minimumPitch || required > bytes || required > MaximumBytes) { error = "Invalid Desktop Duplication pointer buffer or pitch."; return false; }
    Shape result; result.type = type; result.width = width; result.height = height;
    result.rgba.resize(static_cast<size_t>(width) * height * 4);
    for (uint32_t y = 0; y < height; ++y) for (uint32_t x = 0; x < width; ++x) {
        uint8_t* target = result.rgba.data() + (static_cast<size_t>(y) * width + x) * 4;
        if (mono) {
            const uint8_t bit = static_cast<uint8_t>(0x80U >> (x % 8));
            const uint8_t andMask = data[static_cast<size_t>(y) * pitch + x / 8] & bit ? 255 : 0;
            const uint8_t xorMask = data[static_cast<size_t>(y + height) * pitch + x / 8] & bit ? 255 : 0;
            target[0] = target[1] = target[2] = xorMask; target[3] = andMask;
        } else {
            const uint8_t* source = data + static_cast<size_t>(y) * pitch + x * 4;
            if (type == MaskedColor && source[3] != 0 && source[3] != 255) { error = "Invalid masked-color pointer alpha; expected zero or 255."; return false; }
            target[0] = source[2]; target[1] = source[1]; target[2] = source[0]; target[3] = source[3];
        }
    }
    output = std::move(result); return true;
}
inline uint8_t ComposeChannel(uint32_t type, uint8_t background, uint8_t channel, uint8_t alpha) {
    if (type == Monochrome) return static_cast<uint8_t>((background & alpha) ^ channel);
    if (type == MaskedColor) return alpha ? static_cast<uint8_t>(background ^ channel) : channel;
    return static_cast<uint8_t>((static_cast<unsigned>(channel) * alpha + static_cast<unsigned>(background) * (255 - alpha) + 127) / 255);
}
inline bool ParseUnsigned(const std::wstring& text, uint64_t maximum, bool allowHex, uint64_t& output) {
    if (text.empty()) return false;
    size_t start = 0; unsigned base = 10;
    if (allowHex && text.size() > 2 && text[0] == L'0' && (text[1] == L'x' || text[1] == L'X')) { start = 2; base = 16; }
    uint64_t value = 0;
    for (size_t i = start; i < text.size(); ++i) {
        const wchar_t c = text[i]; unsigned digit = 99;
        if (c >= L'0' && c <= L'9') digit = c - L'0';
        else if (c >= L'a' && c <= L'f') digit = c - L'a' + 10;
        else if (c >= L'A' && c <= L'F') digit = c - L'A' + 10;
        if (digit >= base || digit > maximum || value > (maximum - digit) / base) return false;
        value = value * base + digit;
    }
    if (!value) return false;
    output = value; return true;
}
inline std::vector<std::string> Tests(unsigned& count) {
    std::vector<std::string> failures; auto check = [&](bool value, const char* name) { ++count; if (!value) failures.emplace_back(name); };
    Shape shape; std::string error;
    const uint8_t mono[] = {0x30,0x00,0x50,0x00}; // black, white, unchanged, inverted; padded rows
    check(Decode(Monochrome,4,2,2,mono,sizeof(mono),shape,error) && shape.height == 1,"monochrome height includes both masks");
    const uint8_t expected[] = {0,255,90,165};
    for (unsigned i = 0; i < 4; ++i) check(ComposeChannel(shape.type,90,shape.rgba[i*4],shape.rgba[i*4+3]) == expected[i],"monochrome four AND/XOR outcomes");
    const uint8_t color[] = {3,2,1,128,99,99,99,99};
    check(Decode(Color,1,1,8,color,sizeof(color),shape,error) && shape.rgba[0] == 1 && shape.rgba[2] == 3 && shape.rgba[3] == 128,"color BGRA converted to integer RGBA with row padding");
    check(ComposeChannel(Color,0,255,128) == 128,"color alpha blend");
    check(ComposeChannel(MaskedColor,0x55,0x33,255) == 0x66,"masked cursor XOR");
    check(ComposeChannel(MaskedColor,0x55,0x33,0) == 0x33,"masked cursor replace");
    check(!Decode(MaskedColor,1,1,4,color,4,shape,error),"masked cursor invalid intermediate alpha rejected");
    check(!Decode(Monochrome,1,3,1,mono,4,shape,error),"odd monochrome height rejected");
    check(!Decode(Color,1,1,3,color,4,shape,error),"short pixel pitch rejected");
    check(!Decode(Color,2,1,8,color,4,shape,error),"truncated shape rejected");
    check(!Decode(Color,0,1,4,color,4,shape,error),"zero shape rejected");
    check(!Decode(Color,1025,1,4100,color,4,shape,error),"excessive shape rejected");
    check(!Decode(8,1,1,4,color,4,shape,error),"unknown shape rejected");
    check(CropOffset(90,100,640) == -10,"partially clipped pointer retains top-left offset");
    check(CropOffset(INT32_MIN,16384,640) == -1024,"extreme negative pointer cannot overflow shader subtraction");
    check(CropOffset(INT32_MAX,100,640) == 640,"pointer beyond crop stays outside");
    check(CropOffset(125,100,640) == 25,"output pointer maps to cropped coordinates");
    uint64_t parsed = 0;
    check(ParseUnsigned(L"0xFFFFFFFF",UINT32_MAX,true,parsed) && parsed == UINT32_MAX,"hex HWND parse");
    check(!ParseUnsigned(L"0x100000000",UINT32_MAX,true,parsed),"HWND size overflow rejected");
    check(ParseUnsigned(L"4294967295",UINT32_MAX,false,parsed) && parsed == UINT32_MAX,"PID unsigned upper bound");
    check(!ParseUnsigned(L"0x22",UINT32_MAX,false,parsed),"hex PID rejected");
    check(!ParseUnsigned(L"-1",UINT32_MAX,true,parsed),"negative handle rejected");
    check(!ParseUnsigned(L"0",UINT32_MAX,true,parsed),"null handle rejected");
    check(!ParseUnsigned(L"12 ",UINT32_MAX,true,parsed),"handle trailing space rejected");
    return failures;
}
} // namespace projection_cursor
