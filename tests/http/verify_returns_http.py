"""Verify return permissions and workflows; --development enables refund outcome simulation."""

from support import check, grant_admin_permission, login, assert_no_store, register_and_login, request, scalar
from fixtures import create_fulfillment_fixture
import argparse
from uuid import uuid4


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument("--development",action="store_true");args=parser.parse_args()
    marker=uuid4().hex;password="Returns!123456"
    owner=register_and_login(f"return-owner-{marker}@example.invalid",password)
    other=register_and_login(f"return-other-{marker}@example.invalid",password)
    return_email=f"return-admin-{marker}@example.invalid";old=register_and_login(return_email,password)
    ship_email=f"return-ship-{marker}@example.invalid";register_and_login(ship_email,password)
    request("GET","/admin/returns",expected=401);request("GET","/admin/returns",token=owner,expected=403)
    grant_admin_permission(return_email,"return");grant_admin_permission(ship_email,"shipment")
    request("GET","/admin/returns",token=old,expected=403)
    admin=login(return_email,password);ship_admin=login(ship_email,password)
    request("GET","/admin/returns",token=ship_admin,expected=403);request("GET","/admin/shipments",token=admin,expected=403)
    order,payment,shipment,product=create_fulfillment_fixture(owner,args.development);oid=order['id'];sid=shipment['id'];path=f"/orders/{oid}/returns";read=f"/orders/{oid}/return"
    request("GET",read,token=owner,expected=404)
    request("POST",path,token=other,key="key",body={"reason":"damaged"},expected=404)
    request("POST",path,token=owner,key="key",body={"reason":"damaged"},expected=409)
    request("PUT",f"/admin/shipments/{sid}/status",token=ship_admin,body={"status":"Shipped","trackingNumber":"RETURN-"+marker})
    request("PUT",f"/admin/shipments/{sid}/status",token=ship_admin,body={"status":"Delivered"})
    request("POST",path,token=owner,key="key",body={"reason":" "},expected=400)
    request("POST",path,token=owner,body={"reason":"damaged"},expected=400)
    value=request("POST",path,token=owner,key="key",body={"reason":" damaged "},expected=201);rid=value['id']
    check(value['reason']=='damaged' and value['remainingRefundableCents']==5000,"Return response invalid")
    check(request("POST",path,token=owner,key="key",body={"reason":"damaged"})==value,"Return replay changed response")
    request("POST",path,token=owner,key="other",body={"reason":"damaged"},expected=409)
    request("GET",read,token=other,expected=404)
    status=f"/admin/returns/{rid}/status";refund_path=f"/admin/returns/{rid}/refund"
    request("PUT",status,token=owner,body={"status":"Approved"},expected=403)
    request("POST",refund_path,token=ship_admin,key="refund",body={"amountCents":5000},expected=403)
    request("PUT",status,token=admin,body={"status":"Received"},expected=409)
    request("PUT",status,token=admin,body={"status":"Lost"},expected=400)
    approved=request("PUT",status,token=admin,body={"status":"Approved"})
    check(request("PUT",status,token=admin,body={"status":"Approved"})==approved,"Approval replay changed time")
    request("PUT",status,token=admin,body={"status":"Received"})
    request("PUT",status,token=admin,body={"status":"Completed"},expected=409)
    request("POST",refund_path,token=admin,key="refund",body={"amountCents":0},expected=400)
    refund=request("POST",refund_path,token=admin,key="refund",body={"amountCents":5000},expected=201)
    check(request("POST",refund_path,token=admin,key="refund",body={"amountCents":5000})==refund,"Admin refund replay changed result")
    request("PUT",status,token=admin,body={"status":"Completed"},expected=409)
    check(request("GET",read,token=owner)['reservedRefundCents']==5000,"Pending refund not reserved")
    if args.development:
        simulate=f"/dev/refunds/{refund['id']}/simulate"
        request("POST",simulate,token=owner,body={"outcome":"timeout"})
        request("PUT",status,token=admin,body={"status":"Completed"},expected=409)
        request("POST",refund_path,token=admin,key="blocked",body={"amountCents":1},expected=409)
        request("POST",simulate,token=owner,body={"outcome":"failure"})
        refund=request("POST",refund_path,token=admin,key="retry",body={"amountCents":5000},expected=201)
        request("POST",f"/dev/refunds/{refund['id']}/simulate",token=owner,body={"outcome":"success"})
        done=request("PUT",status,token=admin,body={"status":"Completed"})
        check(done['refundedCents']==5000 and done['reservedRefundCents']==0 and done['remainingRefundableCents']==0,"Completion financial totals invalid")
        check(request("PUT",status,token=admin,body={"status":"Completed"})==done,"Completion replay changed timestamp")
        settled=request("POST",refund_path,token=admin,key="retry",body={"amountCents":5000})
        check(settled['id']==refund['id'] and settled['status']=='Succeeded',"Refund key cannot replay after return completion")
        request("POST",refund_path,token=admin,key="new-after-completion",body={"amountCents":5000},expected=409)
        request("POST",refund_path,token=admin,key="retry",body={"amountCents":4000},expected=409)
        check(request("POST",path,token=owner,key="key",body={"reason":"damaged"})==done,"Return replay after completion failed")
    else:
        request("POST",f"/dev/refunds/{refund['id']}/simulate",token=owner,body={"outcome":"success"},expected=404)
    listed=request("GET","/admin/returns?pageSize=50",token=admin)
    check(any(v['id']==rid for v in listed['returns']),"Return absent from admin list")
    request("GET","/admin/returns?status=invalid",token=admin,expected=400)
    check(request("GET","/admin/returns?page=2147483647&pageSize=50",token=admin)['returns']==[],"Return page overflow")
    for route,token in [(read,owner),("/admin/returns",admin)]:assert_no_store(route,token)
    check(scalar(f"SELECT Available FROM dbo.Products WHERE Id={product}")==1,"Return flow changed stock")
    print("Returns HTTP passed: independent permissions, ownership, delivery, replay, transitions, live refund totals, stock")

if __name__=="__main__":main()
