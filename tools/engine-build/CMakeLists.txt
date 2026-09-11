cmake_minimum_required(VERSION 3.20)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
project(realesrgan-ncnn-vulkan CXX C)
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

# ---------------------------------------------------------------------------
# ALH Pro: rebuilt Real-ESRGAN ncnn-vulkan engine.
# Front-end source: xinntao/Real-ESRGAN-ncnn-vulkan (MIT).
# Inference library: Tencent/ncnn (BSD-3), built from a 2025/2026 source tree so
# that the Vulkan backend carries the VK_EXT_robustness2 / VK_KHR_robustness2
# handling that the 2022 official binary lacks (broken output on Blackwell).
# ---------------------------------------------------------------------------
set(NCNN_SOURCE_DIR "D:/deep/_research/downloads/ncnn-master-src/ncnn-master" CACHE PATH "ncnn source tree")
set(MSYS_MINGW "D:/deep/_build_realesrgan/msys64/mingw64" CACHE PATH "MSYS2 mingw64 prefix")

# use our own static-library finders instead of the MSYS2 shared-library configs
list(APPEND CMAKE_MODULE_PATH "${CMAKE_CURRENT_SOURCE_DIR}/cmake")
set(CMAKE_FIND_PACKAGE_PREFER_CONFIG FALSE)

# static OpenMP so no libgomp DLL has to ship with the engine
set(OpenMP_C_LIB_NAMES "gomp" CACHE STRING "" FORCE)
set(OpenMP_CXX_LIB_NAMES "gomp" CACHE STRING "" FORCE)
set(OpenMP_gomp_LIBRARY "${MSYS_MINGW}/lib/libgomp.a" CACHE FILEPATH "" FORCE)

find_package(Threads REQUIRED)
find_package(Vulkan REQUIRED)
find_package(OpenMP)
find_package(WebP REQUIRED)
find_package(glslang REQUIRED)
find_program(GLSLANGVALIDATOR_EXECUTABLE NAMES glslangValidator
    PATHS "${MSYS_MINGW}/bin" "$ENV{VULKAN_SDK}/bin" NO_DEFAULT_PATH)

macro(compile_shader SHADER_SRC)
    set(SHADER_SRC_FULLPATH ${CMAKE_CURRENT_SOURCE_DIR}/${SHADER_SRC})
    get_filename_component(SHADER_SRC_NAME_WE ${SHADER_SRC} NAME_WE)

    set(SHADER_SPV_HEX_FILE ${CMAKE_CURRENT_BINARY_DIR}/${SHADER_SRC_NAME_WE}.spv.hex.h)
    add_custom_command(
        OUTPUT ${SHADER_SPV_HEX_FILE}
        COMMAND ${GLSLANGVALIDATOR_EXECUTABLE}
        ARGS -V -s -x -o ${SHADER_SPV_HEX_FILE} ${SHADER_SRC_FULLPATH}
        DEPENDS ${SHADER_SRC_FULLPATH}
        COMMENT "Building SPIR-V module ${SHADER_SRC_NAME_WE}.spv"
        VERBATIM)
    set_source_files_properties(${SHADER_SPV_HEX_FILE} PROPERTIES GENERATED TRUE)
    list(APPEND SHADER_SPV_HEX_FILES ${SHADER_SPV_HEX_FILE})

    set(SHADER_fp16s_SPV_HEX_FILE ${CMAKE_CURRENT_BINARY_DIR}/${SHADER_SRC_NAME_WE}_fp16s.spv.hex.h)
    add_custom_command(
        OUTPUT ${SHADER_fp16s_SPV_HEX_FILE}
        COMMAND ${GLSLANGVALIDATOR_EXECUTABLE}
        ARGS -DNCNN_fp16_storage=1 -V -s -x -o ${SHADER_fp16s_SPV_HEX_FILE} ${SHADER_SRC_FULLPATH}
        DEPENDS ${SHADER_SRC_FULLPATH}
        COMMENT "Building SPIR-V module ${SHADER_SRC_NAME_WE}_fp16s.spv"
        VERBATIM)
    set_source_files_properties(${SHADER_fp16s_SPV_HEX_FILE} PROPERTIES GENERATED TRUE)
    list(APPEND SHADER_SPV_HEX_FILES ${SHADER_fp16s_SPV_HEX_FILE})

    set(SHADER_int8s_SPV_HEX_FILE ${CMAKE_CURRENT_BINARY_DIR}/${SHADER_SRC_NAME_WE}_int8s.spv.hex.h)
    add_custom_command(
        OUTPUT ${SHADER_int8s_SPV_HEX_FILE}
        COMMAND ${GLSLANGVALIDATOR_EXECUTABLE}
        ARGS -DNCNN_fp16_storage=1 -DNCNN_int8_storage=1 -V -s -x -o ${SHADER_int8s_SPV_HEX_FILE} ${SHADER_SRC_FULLPATH}
        DEPENDS ${SHADER_SRC_FULLPATH}
        COMMENT "Building SPIR-V module ${SHADER_SRC_NAME_WE}_int8s.spv"
        VERBATIM)
    set_source_files_properties(${SHADER_int8s_SPV_HEX_FILE} PROPERTIES GENERATED TRUE)
    list(APPEND SHADER_SPV_HEX_FILES ${SHADER_int8s_SPV_HEX_FILE})
endmacro()

include_directories(${CMAKE_CURRENT_BINARY_DIR})

if(OPENMP_FOUND)
    set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} ${OpenMP_C_FLAGS}")
    set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} ${OpenMP_CXX_FLAGS}")
endif()

# --- ncnn: same feature set as the official Real-ESRGAN front-end build ---
set(NCNN_INSTALL_SDK OFF CACHE BOOL "" FORCE)
set(NCNN_VULKAN ON CACHE BOOL "" FORCE)
set(NCNN_SYSTEM_GLSLANG ON CACHE BOOL "" FORCE)
set(NCNN_VULKAN_ONLINE_SPIRV ON CACHE BOOL "" FORCE)
set(NCNN_BUILD_BENCHMARK OFF CACHE BOOL "" FORCE)
set(NCNN_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(NCNN_BUILD_TOOLS OFF CACHE BOOL "" FORCE)
set(NCNN_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(NCNN_DISABLE_RTTI ON CACHE BOOL "" FORCE)
set(NCNN_DISABLE_EXCEPTION ON CACHE BOOL "" FORCE)
set(NCNN_PIXEL ON CACHE BOOL "" FORCE)
set(NCNN_PIXEL_ROTATE OFF CACHE BOOL "" FORCE)
set(NCNN_PIXEL_AFFINE OFF CACHE BOOL "" FORCE)
set(NCNN_PIXEL_DRAWING OFF CACHE BOOL "" FORCE)
set(NCNN_INT8 OFF CACHE BOOL "" FORCE)

set(WITH_LAYER_absval OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_argmax OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_batchnorm OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_bias OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_bnll OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_concat ON CACHE BOOL "" FORCE)
set(WITH_LAYER_convolution ON CACHE BOOL "" FORCE)
set(WITH_LAYER_crop ON CACHE BOOL "" FORCE)
set(WITH_LAYER_deconvolution OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_dropout OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_eltwise ON CACHE BOOL "" FORCE)
set(WITH_LAYER_elu OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_embed OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_exp OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_flatten ON CACHE BOOL "" FORCE)
set(WITH_LAYER_innerproduct ON CACHE BOOL "" FORCE)
set(WITH_LAYER_input ON CACHE BOOL "" FORCE)
set(WITH_LAYER_log OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_lrn OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_memorydata OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_mvn OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_pooling OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_power OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_prelu ON CACHE BOOL "" FORCE)
set(WITH_LAYER_proposal OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_reduction OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_relu ON CACHE BOOL "" FORCE)
set(WITH_LAYER_reshape OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_roipooling OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_scale OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_sigmoid OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_slice OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_softmax OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_split ON CACHE BOOL "" FORCE)
set(WITH_LAYER_spp OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_tanh OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_threshold OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_tile OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_rnn OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_lstm OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_binaryop ON CACHE BOOL "" FORCE)
set(WITH_LAYER_unaryop OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_convolutiondepthwise OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_padding ON CACHE BOOL "" FORCE)
set(WITH_LAYER_squeeze OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_expanddims OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_normalize OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_permute OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_priorbox OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_detectionoutput OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_interp ON CACHE BOOL "" FORCE)
set(WITH_LAYER_deconvolutiondepthwise OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_shufflechannel OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_instancenorm OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_clip OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_reorg OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_yolodetectionoutput OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_quantize OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_dequantize OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_yolov3detectionoutput OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_psroipooling OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_roialign OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_packing ON CACHE BOOL "" FORCE)
set(WITH_LAYER_requantize OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_cast ON CACHE BOOL "" FORCE)
set(WITH_LAYER_hardsigmoid OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_selu OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_hardswish OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_noop OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_pixelshuffle ON CACHE BOOL "" FORCE)
set(WITH_LAYER_deepcopy OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_mish OFF CACHE BOOL "" FORCE)
set(WITH_LAYER_statisticspooling OFF CACHE BOOL "" FORCE)

add_subdirectory(${NCNN_SOURCE_DIR} ${CMAKE_BINARY_DIR}/ncnn_build)

# --- shaders ---
set(SHADER_SPV_HEX_FILES)
compile_shader(realesrgan_preproc.comp)
compile_shader(realesrgan_postproc.comp)
compile_shader(realesrgan_preproc_tta.comp)
compile_shader(realesrgan_postproc_tta.comp)
add_custom_target(generate-spirv DEPENDS ${SHADER_SPV_HEX_FILES})

add_executable(realesrgan-ncnn-vulkan main.cpp realesrgan.cpp)
add_dependencies(realesrgan-ncnn-vulkan generate-spirv)

# the front-end entry point is wmain() on Windows
target_compile_options(realesrgan-ncnn-vulkan PRIVATE -municode)
target_link_options(realesrgan-ncnn-vulkan PRIVATE -municode)

target_link_libraries(realesrgan-ncnn-vulkan ncnn WebP::webp Vulkan::Vulkan)

# self-contained executable: static runtime, only the system vulkan-1.dll stays external
set_target_properties(realesrgan-ncnn-vulkan PROPERTIES
    LINK_FLAGS "-static -static-libgcc -static-libstdc++")
