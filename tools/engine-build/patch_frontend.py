"""Apply the MinGW compatibility patch to the Real-ESRGAN ncnn-vulkan front-end.

Why: the upstream front-end is written for MSVC, where `%s` inside a *wide*
format string consumes a wchar_t* argument. MinGW follows C99, where `%s` is a
narrow string and wide arguments need `%ls`. Building the official source
unpatched with MinGW makes the model path collapse to its first byte
(e.g. "models" -> "m"), so the engine cannot find <model>.param and produces an
empty/black frame - which looks exactly like the GPU bug we are fixing.

The same block also passed std::to_string(scale) (a narrow std::string) to a
wide %s, which is wrong on every platform.

Usage:
    python patch_frontend.py <path to main.cpp>

The patch is idempotent.
"""
import sys

OLD = '''        swprintf(parampath, 256, L"%s/%s-x%s.param", model.c_str(), modelname.c_str(), std::to_string(scale));
        swprintf(modelpath, 256, L"%s/%s-x%s.bin", model.c_str(), modelname.c_str(), std::to_string(scale));
    }
    else{
        swprintf(parampath, 256, L"%s/%s.param", model.c_str(), modelname.c_str());
        swprintf(modelpath, 256, L"%s/%s.bin", model.c_str(), modelname.c_str());
    }'''

NEW = '''        swprintf(parampath, 256, L"%ls/%ls-x%d.param", model.c_str(), modelname.c_str(), scale);
        swprintf(modelpath, 256, L"%ls/%ls-x%d.bin", model.c_str(), modelname.c_str(), scale);
    }
    else{
        swprintf(parampath, 256, L"%ls/%ls.param", model.c_str(), modelname.c_str());
        swprintf(modelpath, 256, L"%ls/%ls.bin", model.c_str(), modelname.c_str());
    }'''


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 1
    path = sys.argv[1]
    with open(path, 'r', encoding='utf-8', newline='') as fh:
        text = fh.read()
    if NEW in text:
        print('already patched:', path)
        return 0
    if OLD not in text:
        print('ERROR: expected block not found - upstream source changed?', path)
        return 2
    with open(path, 'w', encoding='utf-8', newline='') as fh:
        fh.write(text.replace(OLD, NEW))
    print('patched:', path)
    return 0


if __name__ == '__main__':
    sys.exit(main())
