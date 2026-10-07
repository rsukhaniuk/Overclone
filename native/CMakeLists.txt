cmake_minimum_required(VERSION 3.20)
project(document_engine LANGUAGES CXX)

add_library(document_core STATIC src/document.cpp)
target_include_directories(document_core PUBLIC include)
target_compile_features(document_core PUBLIC cxx_std_17)
target_compile_options(document_core PRIVATE -Wall -Wextra -Wpedantic)

if(EMSCRIPTEN)
    add_executable(engine src/wasm.cpp)
    target_link_libraries(engine PRIVATE document_core)
    set_target_properties(engine PROPERTIES SUFFIX ".mjs")
    target_link_options(engine PRIVATE
        --no-entry
        "-sMODULARIZE=1"
        "-sEXPORT_ES6=1"
        "-sENVIRONMENT=web,worker,node"
        "-sALLOW_MEMORY_GROWTH=1"
        "-sEXPORTED_FUNCTIONS=['_count_words','_malloc','_free']"
        "-sEXPORTED_RUNTIME_METHODS=['ccall']")
else()
    enable_testing()
    add_executable(document_tests tests/document_test.cpp)
    target_link_libraries(document_tests PRIVATE document_core)
    add_test(NAME document_words COMMAND document_tests)
endif()
