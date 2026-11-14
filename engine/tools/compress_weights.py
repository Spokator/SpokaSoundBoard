"""Stores large float32 weights as float16 with a Cast back to float32 in front of each use: the download is halved,
the computation stays float32 (ONNX Runtime folds the casts once, when the session is created)."""
import os
import sys

import numpy as np
import onnx
from onnx import helper, numpy_helper, TensorProto

src_dir, dst_dir = sys.argv[1], sys.argv[2]
os.makedirs(dst_dir, exist_ok=True)
for name in ["content", "regulator", "dit", "f0", "hift"]:
    model = onnx.load(os.path.join(src_dir, name + ".onnx"))
    g = model.graph
    new_inits, casts, converted = [], [], 0
    for init in g.initializer:
        if init.data_type == TensorProto.FLOAT and np.prod(init.dims) >= 4096:
            arr = numpy_helper.to_array(init)
            if np.abs(arr).max() < 60000:  # fits float16
                half = numpy_helper.from_array(arr.astype(np.float16), init.name + "__f16")
                new_inits.append(half)
                casts.append(helper.make_node("Cast", [half.name], [init.name], to=TensorProto.FLOAT, name=init.name + "__cast"))
                converted += 1
                continue
        new_inits.append(init)
    del g.initializer[:]
    g.initializer.extend(new_inits)
    nodes = list(g.node)
    del g.node[:]
    g.node.extend(casts + nodes)
    onnx.checker.check_model(model)
    out = os.path.join(dst_dir, name + ".onnx")
    onnx.save(model, out)
    print(f"{name}: {converted} tensors in float16, {os.path.getsize(out) / 1e6:.1f} MB", flush=True)
