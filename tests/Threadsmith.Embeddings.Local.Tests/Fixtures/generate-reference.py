"""Offline reference output generator; pinned Rust tokenizer + ONNX CPU session."""
import argparse, json, pathlib, time, numpy as np, onnxruntime as ort
from tokenizers import Tokenizer
parser = argparse.ArgumentParser()
parser.add_argument("--assets", type=pathlib.Path, required=True)
parser.add_argument("--output", type=pathlib.Path, required=True)
args = parser.parse_args()
assets = args.assets
tok = Tokenizer.from_file(str(assets / 'tokenizer.json'))
tok.no_padding()
tok.no_truncation()
options = ort.SessionOptions()
options.intra_op_num_threads = 2
options.inter_op_num_threads = 1
options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
options.add_session_config_entry('session.intra_op.allow_spinning', '0')
options.add_session_config_entry('session.inter_op.allow_spinning', '0')
started = time.perf_counter()
session = ort.InferenceSession(str(assets / 'model.onnx'), sess_options=options, providers=['CPUExecutionProvider'])
cold_ms = 1000*(time.perf_counter()-started)
def encode(text, padded=False):
    full = tok.encode(text).ids
    ids = full if len(full)<=256 else full[:255]+[102]
    mask = [1]*len(ids)
    if padded:
        mask += [0]*(256-len(ids))
        ids += [0]*(256-len(ids))
    return full, ids, mask
def vector(text, padded=False):
    full,ids,mask = encode(text,padded)
    inputs={'input_ids':np.array([ids],dtype=np.int64),'attention_mask':np.array([mask],dtype=np.int64),'token_type_ids':np.zeros((1,len(ids)),dtype=np.int64)}
    hidden = session.run(['last_hidden_state'],inputs)[0]
    mean = (hidden * np.array(mask,dtype=np.float32)[None,:,None]).sum(axis=1)[0]/sum(mask)
    return (mean/np.linalg.norm(mean)).tolist()
samples=['', 'Hello, WORLD!', 'Café naïve résumé', '你好 世界 😀', '[CLS] keep special tokens [SEP]', 'line one\nline two\tindent', 'a '*254, 'a '*255]
parity=[]
for text in samples:
    full,ids,mask=encode(text)
    v=vector(text); p=vector(text,True)
    parity.append(dict(text=text,ids=ids,attentionMask=mask,fullTokenCount=len(full),wasTruncated=len(full)>256,vector=v,paddedMaxAbsError=max(abs(a-b) for a,b in zip(v,p))))
args.output.write_text(json.dumps({'revision':'9bc18616990647530c139b95df1d1aa30cd115b7','runtime':'onnxruntime 1.22.1 CPU; Rust tokenizers 0.22.2','coldSessionLoadMs':cold_ms,'samples':parity},indent=2),encoding='utf-8')
print(json.dumps({'coldSessionLoadMs':cold_ms,'shapes':[(i.name,i.shape) for i in session.get_inputs()],'maxDynamicVsPaddedError':max(p['paddedMaxAbsError'] for p in parity)}))
