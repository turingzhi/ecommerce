"""Verify fulfillment lists, admin history, and owner tracking; optional --development."""

from support import BASE_URL, check, grant_admin_permission, login, assert_no_store, register_and_login, request, scalar
from fixtures import create_fulfillment_fixture
import argparse
from uuid import uuid4
from urllib.request import Request, urlopen
from urllib.error import HTTPError








def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument("--development",action="store_true");args=parser.parse_args()
    marker=uuid4().hex;password="Fulfillment!123456"
    owner=register_and_login(f"fulfill-owner-{marker}@example.invalid",password)
    other=register_and_login(f"fulfill-other-{marker}@example.invalid",password)
    email=f"fulfill-admin-{marker}@example.invalid";old=register_and_login(email,password)
    request("GET","/admin/shipments",expected=401);request("GET","/admin/shipments",token=owner,expected=403)
    grant_admin_permission(email,"shipment");request("GET","/admin/shipments",token=old,expected=403);admin=login(email,password)
    order,payment,shipment,product=create_fulfillment_fixture(owner,args.development);sid=shipment['id'];oid=order['id']
    history_path=f"/admin/shipments/{sid}/history";tracking_path=f"/orders/{oid}/tracking"
    request("GET",history_path,token=owner,expected=403);request("GET",tracking_path,token=other,expected=404)
    request("GET",f"/orders/{uuid4()}/tracking",token=owner,expected=404)
    initial=request("GET",history_path,token=admin)['history'];check(len(initial)==(1 if args.development else 0),"Unexpected initial history")
    request("PUT",f"/admin/shipments/{sid}/status",token=admin,body={"status":"Shipped","trackingNumber":"TRACK-"+marker,"actorId":"spoofed"})
    request("PUT",f"/admin/shipments/{sid}/status",token=admin,body={"status":"Delivered"})
    request("PUT",f"/admin/shipments/{sid}/status",token=admin,body={"status":"Delivered"})
    history=request("GET",history_path,token=admin)['history'];check(len(history)==len(initial)+2,"Status replay added history")
    check([h['toStatus'] for h in history[-2:]]==['Shipped','Delivered'],"Wrong history order")
    check(all(h['actorId'] and h['actorId']!='spoofed' and h['occurredAt'].endswith('Z') for h in history[-2:]),"History actor or UTC invalid")
    if initial:check(initial[0]['actorId'] is None,"Initial history has actor")
    check(scalar(f"SELECT COUNT(*) FROM dbo.ShipmentHistory h JOIN dbo.AspNetUsers u ON h.ActorId=u.Id WHERE h.ShipmentId='{sid}' AND u.Email='{email}'")==2,"History actor does not match authenticated admin")
    tracking=request("GET",tracking_path,token=owner);check(tracking['shipment']['status']=='Delivered',"Tracking shipment stale")
    check(all('actorId' not in h for h in tracking['history']),"Customer received actor identities")
    check(len(tracking['history'])==len(history),"Tracking history mismatch")
    page=request("GET","/admin/shipments?status=Delivered&page=1&pageSize=50",token=admin)
    check(any(s['id']==sid for s in page['shipments']),"Delivered fixture absent from list")
    check(all(s['status']=='Delivered' for s in page['shipments']),"List filter failed")
    check(request("GET","/admin/shipments?page=2147483647&pageSize=50",token=admin)['shipments']==[],"Page overflow")
    check(request("GET","/admin/shipments?page=0&pageSize=0",token=admin)['pageSize']==1,"Paging minima")
    for state in ["Pending","Shipped","Delivered"]:
        filtered=request("GET","/admin/shipments?status="+state,token=admin)
        check(all(s['status']==state for s in filtered['shipments']),"Shipment status filter mismatch")
    for suffix in ["?page=bad","?page=2147483648","?pageSize=bad"]:
        try:
            with urlopen(Request(BASE_URL+"/admin/shipments"+suffix,headers={"Authorization":"Bearer "+admin}),timeout=15) as response:
                raise AssertionError("Malformed numeric query accepted")
        except HTTPError as error:check(error.code==400,"Malformed numeric query not rejected")
    request("GET","/admin/shipments?status=lost",token=admin,expected=400)
    request("GET",f"/admin/shipments/{uuid4()}/history",token=admin,expected=404)
    for path,token in [(tracking_path,owner),(history_path,admin),("/admin/shipments",admin)]:assert_no_store(path,token)
    print("Fulfillment HTTP passed: permissions, filters, history, replay, trusted actors, owner tracking, UTC, no-store")

if __name__=="__main__":main()
