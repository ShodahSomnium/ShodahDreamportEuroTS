# input: name px py pz nx ny nz   (unity, y already relative)  -> "name,x,y,z,yaw,pitch"
function atan2d(y,x){ return atan2(y,x)*57.29578 }
{ name=$1; px=$2; py=$3; pz=$4; nx=$5; ny=$6; nz=$7;
  if (ny>0.7 || ny<-0.7) { cx=px+5; cy=py+4; cz=pz+5 }
  else { l=sqrt(nx*nx+nz*nz); hx=nx/l; hz=nz/l; tx=hz; tz=-hx; cx=px+hx*4.5+tx*4.5; cy=py+0.6; cz=pz+hz*4.5+tz*4.5 }
  dx=px-cx; dy=py-cy; dz=pz-cz; yaw=atan2d(dx,dz); pitch=-atan2d(dy, sqrt(dx*dx+dz*dz));
  printf "%s%s,%.2f,%.2f,%.2f,%.1f,%.1f", (NR>1?";":""), name, cx, cy, cz, yaw, pitch }
