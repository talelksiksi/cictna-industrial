cmake_minimum_required(VERSION 3.20)
project(cictna_ua C)

# Built by native/build.sh or native/build.ps1, which install mbedTLS and
# open62541 into a prefix and pass it through CMAKE_PREFIX_PATH.
find_package(open62541 REQUIRED CONFIG)

set(CICTNA_UA_OUT_DIR "${CMAKE_BINARY_DIR}/out" CACHE PATH "Where the shared library is written")

add_library(cictna_ua SHARED cictna_ua.c cictna_ua.h)
target_link_libraries(cictna_ua PRIVATE open62541::open62541)
set_target_properties(cictna_ua PROPERTIES
    C_STANDARD 99
    C_VISIBILITY_PRESET hidden
    RUNTIME_OUTPUT_DIRECTORY "${CICTNA_UA_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${CICTNA_UA_OUT_DIR}"
    # Multi-config generators (Visual Studio) append the config name otherwise.
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CICTNA_UA_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${CICTNA_UA_OUT_DIR}")

if(WIN32)
    # mbedTLS 3.x uses BCrypt for entropy; open62541 needs Winsock and IP helper.
    target_link_libraries(cictna_ua PRIVATE ws2_32 iphlpapi bcrypt)
else()
    target_compile_options(cictna_ua PRIVATE -Wall -Wextra)
    # Export only our cictna_ua_* functions, never the statically linked
    # open62541 or mbedTLS symbols, and strip the release binary.
    target_link_options(cictna_ua PRIVATE
        "-Wl,--exclude-libs,ALL"
        "-Wl,--no-undefined"
        "$<$<CONFIG:Release>:-s>")
endif()
