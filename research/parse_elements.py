import struct,sys,json,os
d=open(sys.argv[1],'rb').read()
res=sys.argv[2]
lay=json.load(open(f'{res}/layouts/v165layouts/v165/layout.json',encoding='utf-8'))
mk={m['before']:m['kind'] for m in lay['markers']}
p=8; schema_ok=0; mism=[]
for i in range(lay['listCount']):
    k=mk.get(i)
    if k=='checksum': p+=8
    elif k=='exporter':
        a,n=struct.unpack_from('<II',d,p); p+=8+n+4
    elif k=='tag':
        print('tag before',i,'raw',d[p:p+48].hex()); 
        a,n=struct.unpack_from('<II',d,p); p+=8+n
    sz,cnt=struct.unpack_from('<II',d,p)
    js=f'{res}/layouts/v165/list_{i}.json'
    if os.path.exists(js):
        s=json.load(open(js,encoding='utf-8'))
        if s['size']!=sz: mism.append((i,s['struct'],s['size'],sz))
        else: schema_ok+=1
    if sz>1_000_000 or p+8+sz*cnt>len(d): print('BROKEN at list',i,hex(p),sz,cnt); break
    p+=8+sz*cnt
print('end',hex(p),'filesize',hex(len(d)),'remaining',len(d)-p)
print('schemas matching size:',schema_ok,'mismatch:',mism)
print('tail raw',d[p:p+64].hex())
