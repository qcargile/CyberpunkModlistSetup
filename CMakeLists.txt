cmake_minimum_required(VERSION 3.21)

if(NOT DEFINED VCPKG_TARGET_TRIPLET)
    set(VCPKG_TARGET_TRIPLET "x64-windows-static-md" CACHE STRING "")
endif()

project(CyberpunkModlistSetup VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT WIN32)
    message(FATAL_ERROR "Windows-only.")
endif()

find_package(imgui CONFIG REQUIRED)
find_package(simdjson CONFIG REQUIRED)

add_executable(CyberpunkModlistSetup WIN32
    src/main_gui.cpp
    src/Catalog.cpp
    src/Config.cpp
    src/Detect.cpp
    src/Net.cpp
    src/Run.cpp
    src/SystemTweaks.cpp
    src/Verify.cpp
    src/CleanCore.cpp
    src/GameDetect.cpp
    src/Quarantine.cpp
    src/resources.rc
)

target_include_directories(CyberpunkModlistSetup PRIVATE src)

target_link_libraries(CyberpunkModlistSetup PRIVATE
    imgui::imgui
    simdjson::simdjson
    d3d11
    dxgi
    shell32
    advapi32
    winhttp
    wintrust
    crypt32
    ole32
    windowscodecs
)

target_compile_definitions(CyberpunkModlistSetup PRIVATE
    NOMINMAX
    WIN32_LEAN_AND_MEAN
    _CRT_SECURE_NO_WARNINGS
)

set_property(TARGET CyberpunkModlistSetup PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")

target_link_options(CyberpunkModlistSetup PRIVATE /MANIFEST:NO)

add_custom_target(package
    COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_SOURCE_DIR}/dist/CyberpunkModlistSetup"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:CyberpunkModlistSetup> "${CMAKE_CURRENT_SOURCE_DIR}/dist/CyberpunkModlistSetup/"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different "${CMAKE_CURRENT_SOURCE_DIR}/README.md" "${CMAKE_CURRENT_SOURCE_DIR}/dist/CyberpunkModlistSetup/"
    COMMAND ${CMAKE_COMMAND} -E copy_directory "${CMAKE_CURRENT_SOURCE_DIR}/art" "${CMAKE_CURRENT_SOURCE_DIR}/dist/CyberpunkModlistSetup/art"
    DEPENDS CyberpunkModlistSetup
)
