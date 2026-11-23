import csv,heapq,math
import re,sys
# walls are read from the real map asset (run from the project folder: python results/diagnosis/m3_geometry_check.py)
txt=open('Assets/Maps/M3_Corridor.asset').read()
walls=[]
for m in re.finditer(r"position: \{x: (-?[\d.]+), y: (-?[\d.]+)\}\s+rotationDeg: (-?[\d.]+)\s+size: \{x: ([\d.]+), y: ([\d.]+)\}",txt):
    x,z,r,sx,sz=map(float,m.groups()); assert r==0; walls.append((x,z,sx,sz))
print(len(walls),"wall boxes read from the asset")
h=0.02; N=300
def c(i): return -3+h*(i+.5)
def free(i,j,r):
    x,z=c(i),c(j)
    if abs(x)>3-r or abs(z)>3-r: return False
    for cx,cz,sx,sz in walls:
        dx=max(abs(x-cx)-sx/2,0); dz=max(abs(z-cz)-sz/2,0)
        if math.hypot(dx,dz)<=r: return False
    return True
def solve(r,sx,sz):
    F=[[free(i,j,r) for j in range(N)] for i in range(N)]
    si,sj=int((sx+3)/h),int((sz+3)/h)
    if not F[si][sj]: return None
    D={(si,sj):0.0}; pq=[(0.0,si,sj)]
    while pq:
        d,i,j=heapq.heappop(pq)
        if d>D[(i,j)]: continue
        if 2.0<=c(i)<=3.0 and 2.0<=c(j)<=3.0: return d    # goal zone (centre inside the 1x1 m zone)
        for di in(-1,0,1):
            for dj in(-1,0,1):
                if di==dj==0: continue
                a,b=i+di,j+dj
                if 0<=a<N and 0<=b<N and F[a][b]:
                    nd=d+h*math.hypot(di,dj)
                    if nd<D.get((a,b),1e9): D[(a,b)]=nd; heapq.heappush(pq,(nd,a,b))
    return -1
for r in (0.10,0.125):
    print("radius",r,"centre of start zone ->goal zone:",solve(r,-2.5,-2.5))
rows=list(csv.DictReader(open('results/start_poses.csv')))
for r in (0.125,):
    L=[solve(r,float(p['x']),float(p['z'])) for p in rows]
    print("30 seeds, radius %.3f: unreachable=%d  shortest(min/mean/max)=%.2f/%.2f/%.2f m"%(r,sum(1 for l in L if l in(None,-1)),min(L),sum(L)/len(L),max(L)))
