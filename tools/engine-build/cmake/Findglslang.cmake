# Minimal glslang finder for the ALH Pro engine build.
# Prefers the MSYS2 *static* archives so the shipped exe needs no glslang DLLs.
if(NOT MSYS_MINGW)
    set(MSYS_MINGW "D:/deep/_build_realesrgan/msys64/mingw64" CACHE PATH "MSYS2 mingw64 prefix")
endif()

set(GLSLANG_INCLUDE_DIR "${MSYS_MINGW}/include")
set(GLSLANG_LIBRARY "${MSYS_MINGW}/lib/libglslang.a")
set(SPIRV_LIBRARY "${MSYS_MINGW}/lib/libSPIRV.a")
set(SPIRV_TOOLS_LIBRARY "${MSYS_MINGW}/lib/libSPIRV-Tools.a")
set(SPIRV_TOOLS_OPT_LIBRARY "${MSYS_MINGW}/lib/libSPIRV-Tools-opt.a")
set(GLSLANG_RESOURCE_LIMITS_LIBRARY "${MSYS_MINGW}/lib/libglslang-default-resource-limits.a")

if(EXISTS "${GLSLANG_LIBRARY}" AND EXISTS "${SPIRV_LIBRARY}")
    if(NOT TARGET glslang::glslang)
        add_library(glslang::glslang STATIC IMPORTED GLOBAL)
        set_target_properties(glslang::glslang PROPERTIES
            IMPORTED_LOCATION "${GLSLANG_LIBRARY}"
            INTERFACE_INCLUDE_DIRECTORIES "${GLSLANG_INCLUDE_DIR}"
            INTERFACE_LINK_LIBRARIES "${SPIRV_LIBRARY};${GLSLANG_RESOURCE_LIMITS_LIBRARY};${SPIRV_TOOLS_OPT_LIBRARY};${SPIRV_TOOLS_LIBRARY}")

        add_library(glslang::SPIRV STATIC IMPORTED GLOBAL)
        set_target_properties(glslang::SPIRV PROPERTIES
            IMPORTED_LOCATION "${SPIRV_LIBRARY}"
            INTERFACE_INCLUDE_DIRECTORIES "${GLSLANG_INCLUDE_DIR}"
            INTERFACE_LINK_LIBRARIES "${SPIRV_LIBRARY}")
    endif()

    set(glslang_VERSION "system-static")
    set(glslang_FOUND TRUE)
    message(STATUS "Using static glslang: ${GLSLANG_LIBRARY}")
else()
    set(glslang_FOUND FALSE)
    message(STATUS "static glslang not found under ${MSYS_MINGW}/lib")
endif()
