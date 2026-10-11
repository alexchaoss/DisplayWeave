#include "ProjectionInput.h"
#include <cstdio>
int main(){
    unsigned count=0;auto failures=projection_input::policy::Tests(count);
    for(const auto& failure:failures)std::fprintf(stderr,"FAIL %s\n",failure.c_str());
    std::printf("Projection input policy: %u passed; %u failed. No input, windows, guardian or capture created.\n",
        count-static_cast<unsigned>(failures.size()),static_cast<unsigned>(failures.size()));
    return failures.empty()?0:1;
}
