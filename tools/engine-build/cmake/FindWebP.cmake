# Minimal WebP finder for the ALH Pro engine build (MSYS2 static archives).
if(NOT MSYS_MINGW)
    set(MSYS_MINGW "D:/deep/_build_realesrgan/msys64/mingw64" CACHE PATH "MSYS2 mingw64 prefix")
endif()

set(WebP_INCLUDE_DIR "${MSYS_MINGW}/include")
set(WEBP_LIBRARY "${MSYS_MINGW}/lib/libwebp.a")
set(SHARPYUV_LIBRARY "${MSYS_MINGW}/lib/libsharpyuv.a")

if(EXISTS "${WEBP_LIBRARY}")
    if(NOT TARGET WebP::webp)
        add_library(WebP::webp STATIC IMPORTED GLOBAL)
        set_target_properties(WebP::webp PROPERTIES
            IMPORTED_LOCATION "${WEBP_LIBRARY}"
            INTERFACE_INCLUDE_DIRECTORIES "${WebP_INCLUDE_DIR}"
            INTERFACE_LINK_LIBRARIES "${SHARPYUV_LIBRARY}")
    endif()
    set(WebP_FOUND TRUE)
    message(STATUS "Using static WebP: ${WEBP_LIBRARY}")
else()
    set(WebP_FOUND FALSE)
    message(STATUS "static WebP not found under ${MSYS_MINGW}/lib")
endif()
