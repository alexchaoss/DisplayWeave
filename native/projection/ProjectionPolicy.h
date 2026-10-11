#pragma once
#include <cstdint>
#include <string>
#include <vector>
#include <limits>
#include <cmath>

namespace projection {
struct Rectangle { int32_t x = 0, y = 0, width = 0, height = 0; };
constexpr int kMaximumDimension = 16384;
constexpr int64_t kMaximumPixels = 64LL * 1024 * 1024;

inline bool ParseInteger(const std::wstring& text, int32_t& result) {
    if (text.empty()) return false;
    size_t offset = text[0] == L'-' ? 1 : 0;
    if (offset == text.size()) return false;
    uint64_t value = 0;
    const uint64_t limit = offset ? 2147483648ULL : 2147483647ULL;
    for (; offset < text.size(); ++offset) {
        if (text[offset] < L'0' || text[offset] > L'9') return false;
        const unsigned digit = static_cast<unsigned>(text[offset] - L'0');
        if (value > (limit - digit) / 10) return false;
        value = value * 10 + digit;
    }
    result = text[0] == L'-' ? static_cast<int32_t>(-static_cast<int64_t>(value)) : static_cast<int32_t>(value);
    return true;
}
inline bool ValidRectangle(const Rectangle& value) {
    if (value.width <= 0 || value.height <= 0 || value.width > kMaximumDimension || value.height > kMaximumDimension) return false;
    if (static_cast<int64_t>(value.width) * value.height > kMaximumPixels) return false;
    const int64_t right = static_cast<int64_t>(value.x) + value.width;
    const int64_t bottom = static_cast<int64_t>(value.y) + value.height;
    return right <= INT32_MAX && right >= INT32_MIN && bottom <= INT32_MAX && bottom >= INT32_MIN;
}
inline bool ParseRectangle(const std::wstring& text, Rectangle& result) {
    int32_t parts[4]{};
    size_t begin = 0;
    for (size_t i = 0; i < 4; ++i) {
        const size_t end = text.find(L',', begin);
        if ((i == 3) != (end == std::wstring::npos)) return false;
        if (!ParseInteger(text.substr(begin, end == std::wstring::npos ? end : end - begin), parts[i])) return false;
        begin = end == std::wstring::npos ? text.size() : end + 1;
    }
    Rectangle candidate{parts[0], parts[1], parts[2], parts[3]};
    if (!ValidRectangle(candidate)) return false;
    result = candidate;
    return true;
}
inline int64_t Right(const Rectangle& r) { return static_cast<int64_t>(r.x) + r.width; }
inline int64_t Bottom(const Rectangle& r) { return static_cast<int64_t>(r.y) + r.height; }
inline bool Equal(const Rectangle& a, const Rectangle& b) { return a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height; }
inline bool Contains(const Rectangle& outer, const Rectangle& inner) {
    return ValidRectangle(outer) && ValidRectangle(inner) && inner.x >= outer.x && inner.y >= outer.y && Right(inner) <= Right(outer) && Bottom(inner) <= Bottom(outer);
}
inline bool Overlaps(const Rectangle& a, const Rectangle& b) {
    return ValidRectangle(a) && ValidRectangle(b) && a.x < Right(b) && b.x < Right(a) && a.y < Bottom(b) && b.y < Bottom(a);
}
inline bool ValidateProjection(const Rectangle& source, const Rectangle& destinationDisplay, const Rectangle& destination,
                               bool fixture, const Rectangle& fixtureCrop, std::string& reason) {
    if (!ValidRectangle(source) || !ValidRectangle(destinationDisplay) || !ValidRectangle(destination)) { reason = "Invalid or excessive display dimensions."; return false; }
    if (!Contains(destinationDisplay, destination)) { reason = "Destination must be entirely inside one attached output."; return false; }
    const Rectangle crop = fixture ? fixtureCrop : source;
    if (!Contains(source, crop)) { reason = "Fixture crop must be entirely inside the selected source output."; return false; }
    if (Overlaps(crop, destination)) { reason = fixture ? "Fixture crop and destination overlap." : "Source output and destination overlap; use a separate output or an explicit owned-fixture crop."; return false; }
    reason.clear();
    return true;
}
struct Fit { float x = 0, y = 0, width = 0, height = 0; };
inline Fit AspectFit(int sw, int sh, int dw, int dh) {
    if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) return {};
    const double sx = static_cast<double>(dw) / sw, sy = static_cast<double>(dh) / sh;
    const double scale = sx < sy ? sx : sy;
    const float width = static_cast<float>(sw * scale), height = static_cast<float>(sh * scale);
    return {(dw - width) * 0.5f, (dh - height) * 0.5f, width, height};
}
// Reference math for the explicitly opted-in HDR fixture. Windows reports its
// SDR white as (nits / 80) * 1000; scRGB uses linear 1.0 == 80 nits. Values above
// SDR white are clipped here, not tone-mapped or preserved as HDR.
inline bool SdrWhiteScale(uint32_t whiteLevel, double& scale) {
    if (!whiteLevel) return false;
    scale = 1000.0 / whiteLevel;
    return std::isfinite(scale) && scale > 0;
}
inline double LinearToSrgb(double linear) {
    const double value = linear < 0 ? 0 : (linear > 1 ? 1 : linear);
    return value <= 0.0031308 ? 12.92 * value : 1.055 * std::pow(value, 1.0 / 2.4) - 0.055;
}
inline std::vector<std::string> PolicyTests(unsigned& count) {
    std::vector<std::string> failures;
    auto check = [&](bool condition, const char* name) { ++count; if (!condition) failures.emplace_back(name); };
    int32_t value = 0;
    check(ParseInteger(L"-2147483648", value) && value == INT32_MIN, "integer minimum");
    check(ParseInteger(L"2147483647", value) && value == INT32_MAX, "integer maximum");
    check(!ParseInteger(L"2147483648", value), "positive overflow");
    check(!ParseInteger(L"-2147483649", value), "negative overflow");
    check(!ParseInteger(L"9999999999999999999999999", value), "large integer overflow");
    check(!ParseInteger(L"1x", value), "integer suffix");
    check(!ParseInteger(L" 1", value), "integer whitespace");
    check(!ParseInteger(L"+1", value), "integer plus");
    check(!ParseInteger(L"-", value), "integer no digits");
    Rectangle r;
    check(ParseRectangle(L"-1920,-100,1920,1080", r) && r.x == -1920, "negative coordinates");
    check(!ParseRectangle(L"1,2,3", r), "missing rectangle part");
    check(!ParseRectangle(L"1,2,3,4,5", r), "extra rectangle part");
    check(!ParseRectangle(L"1,,3,4", r), "empty rectangle part");
    check(!ParseRectangle(L"0,0,0,10", r), "zero width");
    check(!ParseRectangle(L"0,0,10,-1", r), "negative height");
    check(!ParseRectangle(L"0,0,16385,10", r), "texture dimension limit");
    check(!ParseRectangle(L"0,0,16384,16384", r), "pixel memory bound");
    check(!ParseRectangle(L"2147483640,0,100,1", r), "right overflow");
    check(!ParseRectangle(L"0,2147483640,1,100", r), "bottom overflow");
    check(ParseRectangle(L"-2147483648,0,1,1", r), "minimum coordinate valid");
    const Rectangle source{0,0,1920,1080}, other{1920,0,1920,1080}, crop{0,0,640,480};
    std::string reason;
    check(ValidateProjection(source, other, {1920,0,640,480}, false, {}, reason), "separate monitor projection");
    check(!ValidateProjection(source, source, {640,0,640,480}, false, {}, reason), "normal same-output rejected");
    check(ValidateProjection(source, source, {640,0,640,480}, true, crop, reason), "fixture adjacent crop accepted");
    check(!ValidateProjection(source, source, {639,0,640,480}, true, crop, reason), "one-pixel crop overlap rejected");
    check(!ValidateProjection(source, source, {640,0,640,480}, true, {-1,0,640,480}, reason), "fixture crop outside rejected");
    check(!ValidateProjection(source, source, {1800,0,640,480}, true, crop, reason), "destination outside rejected");
    check(!Overlaps({-640,0,640,480}, crop), "half-open negative boundary");
    check(Overlaps({-639,0,640,480}, crop), "negative intersection");
    check(Contains(source, source), "exact containment");
    check(!Contains(source, {0,0,1921,1080}), "right outside containment");
    Fit fit = AspectFit(640,480,800,800);
    check(fit.x == 0 && fit.y == 100 && fit.width == 800 && fit.height == 600, "letterbox geometry");
    fit = AspectFit(480,640,800,800);
    check(fit.x == 100 && fit.y == 0 && fit.width == 600 && fit.height == 800, "pillarbox geometry");
    fit = AspectFit(640,480,640,480);
    check(fit.x == 0 && fit.y == 0 && fit.width == 640 && fit.height == 480, "one-to-one geometry");
    check(AspectFit(0,480,640,480).width == 0, "invalid aspect source");
    double whiteScale = 0;
    check(!SdrWhiteScale(0,whiteScale), "unknown SDR white rejected");
    check(SdrWhiteScale(1000,whiteScale) && whiteScale == 1, "80 nit white scale");
    check(SdrWhiteScale(2500,whiteScale) && std::abs(whiteScale-0.4) < 0.000001, "200 nit white scale");
    check(LinearToSrgb(-0.25) == 0, "negative scRGB clips to black");
    check(std::abs(LinearToSrgb(2)-1) < 0.000001, "HDR highlight clips to white");
    check(std::abs(LinearToSrgb(0.0031308)-0.040449936) < 0.0000001, "sRGB transfer breakpoint");
    const unsigned grayBytes[] = {32,64,128,192};
    for (unsigned gray : grayBytes) {
        const double srgb = gray/255.0;
        const double linear = std::pow((srgb+0.055)/1.055,2.4);
        check(std::abs(LinearToSrgb(linear*2.5*whiteScale)*255.0-gray) < 0.01, "gray at 200 nits returns original SDR byte");
    }
    check(std::abs(LinearToSrgb(0.2158605001*2.5)*255.0-128) > 40, "gray test detects missing white normalization");
    return failures;
}
} // namespace projection
