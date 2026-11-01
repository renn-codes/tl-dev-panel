# Build Guidelines
This skill is relavent to compiling and installing the server codebase. It details the Build System, and Common Build Commands.

## Build System
This project is now using `ninja` as its build system, over the Azerothcore project's preferred `cmake`. You should adapt potential issues with the build system itself from `cmake` to it's likely equivalent's cause in `ninja`, if they are to ocurr.

## Common Build System Commands
- Configure: `cmake ../ -G Ninja -DCMAKE_INSTALL_PREFIX=/roc/claude/live/ -DCMAKE_C_COMPILER=/usr/bin/clang -DCMAKE_CXX_COMPILER=/usr/bin/clang++ -DWITH_WARNINGS=0 -DTOOLS_BUILD=all -DSCRIPTS=static -DMODULES=static`
- Build: `ninja -j$(( $(nproc) - 1 ))`
- Install: `ninja install`
- Clean: `ninja -t clean`